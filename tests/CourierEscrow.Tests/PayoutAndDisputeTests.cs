using CourierEscrow.Application;
using CourierEscrow.Application.Services;
using CourierEscrow.Domain;
using CourierEscrow.Domain.Ledger;
using CourierEscrow.Tests.Fakes;

namespace CourierEscrow.Tests;

public class PayoutTests
{
    [Fact]
    public async Task Due_payouts_are_submitted_with_their_stable_reference_to_the_right_wallet()
    {
        var env = await TestEnv.NewAsync();
        var e = await env.ReleasedOrderAsync();

        Assert.Equal(2, await env.Payouts.ExecuteDueAsync());

        Assert.Equal(2, env.Provider.PayoutCalls.Count);
        var seller = env.Provider.PayoutCalls.Single(c => c.Amount == 29_400_000);
        Assert.Equal("265991234567", seller.Msisdn);
        Assert.Equal("Chikondi Phones", seller.BeneficiaryName);
        Assert.Equal(MobileNetwork.Airtel.ToString(), seller.Network);
        Assert.True(env.Db.Payouts.Rows.All(p => p.Status == PayoutStatus.Submitted && p.ProviderRef != null));
        Assert.Equal(0, await env.Payouts.ExecuteDueAsync()); // nothing left to do
    }

    [Fact]
    public async Task A_confirmed_payout_clears_the_payable_and_the_order_settles_when_all_are_confirmed()
    {
        var env = await TestEnv.NewAsync();
        var e = await env.ReleasedOrderAsync();
        await env.Payouts.ExecuteDueAsync();

        var seller = env.Db.Payouts.Rows.Single(p => p.Kind == PayoutKind.Seller);
        await env.Payouts.ApplyAsync(new PayoutNotification("payout.success:1", seller.Reference, PayoutEventKind.Succeeded, seller.Amount, 100_000, null, "TX-S"), null);
        Assert.Equal(EscrowState.Released, e.State);               // courier payout still pending
        Assert.Equal(0, env.Db.Balance(Accounts.SellerPayable(e.SellerId)));

        var courier = env.Db.Payouts.Rows.Single(p => p.Kind == PayoutKind.Courier);
        await env.Payouts.ApplyAsync(new PayoutNotification("payout.success:2", courier.Reference, PayoutEventKind.Succeeded, courier.Amount, 100_000, null, "TX-C"), null);

        Assert.Equal(EscrowState.Settled, e.State);
        Assert.NotNull(e.SettledAt);
        var summary = await env.Repos.GetSummaryAsync("MWK", default);
        Assert.Equal(0, summary.OwedToSellers);
        Assert.Equal(0, summary.OwedToCouriers);
        Assert.Equal(200_000, summary.ProviderFees);
        Assert.Equal(600_000 - 200_000, summary.ProviderBalance);       // platform revenue less the provider's charges
        Assert.True(summary.IsBalanced);
    }

    [Fact]
    public async Task The_same_payout_confirmation_twice_posts_once()
    {
        var env = await TestEnv.NewAsync();
        var e = await env.ReleasedOrderAsync();
        await env.Payouts.ExecuteDueAsync();
        var seller = env.Db.Payouts.Rows.Single(p => p.Kind == PayoutKind.Seller);
        var n = new PayoutNotification("payout.success:1", seller.Reference, PayoutEventKind.Succeeded, seller.Amount, 0, null, "TX");

        Assert.Equal(CallbackOutcome.Processed, await env.Payouts.ApplyAsync(n, null));
        Assert.Equal(CallbackOutcome.Ignored, await env.Payouts.ApplyAsync(n, null));
        Assert.Equal(1, env.Db.Ledger.Rows.Count(t => t.Kind == "payout"));
    }

    [Fact]
    public async Task A_payout_confirmed_for_the_wrong_amount_is_not_trusted()
    {
        var env = await TestEnv.NewAsync();
        var e = await env.ReleasedOrderAsync();
        await env.Payouts.ExecuteDueAsync();
        var seller = env.Db.Payouts.Rows.Single(p => p.Kind == PayoutKind.Seller);

        var outcome = await env.Payouts.ApplyAsync(new PayoutNotification("payout.success:1", seller.Reference, PayoutEventKind.Succeeded, seller.Amount - 100, 0, null, "TX"), null);

        Assert.Equal(CallbackOutcome.AmountMismatch, outcome);
        Assert.Equal(PayoutStatus.NeedsReview, seller.Status);
        Assert.Contains("mismatch", e.OpsFlag);
        Assert.Equal(0, env.Db.Ledger.Rows.Count(t => t.Kind == "payout"));
    }

    [Fact]
    public async Task Temporary_provider_errors_retry_with_the_same_key_then_go_to_review()
    {
        var env = await TestEnv.NewAsync();
        var e = await env.ReleasedOrderAsync();
        var seller = env.Db.Payouts.Rows.Single(p => p.Kind == PayoutKind.Seller);
        var key = seller.Reference;

        for (var i = 0; i < Payout.MaxAttempts; i++)
        {
            env.Provider.PayoutResults.Enqueue(new PayoutSubmission(PayoutSubmissionOutcome.Transient, null, "503")); // the seller's; the courier gets the default "accepted"
            await env.Payouts.ExecuteDueAsync();
            env.Clock.Advance(TimeSpan.FromHours(2));
        }

        Assert.Equal(PayoutStatus.NeedsReview, seller.Status);
        Assert.Equal(key, seller.Reference);
        Assert.True(env.Provider.PayoutCalls.Where(c => c.Amount == seller.Amount).All(c => c.Reference == key));
    }

    [Fact]
    public async Task A_provider_refusal_goes_straight_to_review_and_ops_can_retry_with_a_corrected_wallet()
    {
        var env = await TestEnv.NewAsync();
        var e = await env.ReleasedOrderAsync();
        env.Provider.PayoutResults.Enqueue(new PayoutSubmission(PayoutSubmissionOutcome.Rejected, null, "Invalid account"));
        await env.Payouts.ExecuteDueAsync();

        var seller = env.Db.Payouts.Rows.Single(p => p.Kind == PayoutKind.Seller);
        Assert.Equal(PayoutStatus.NeedsReview, seller.Status);
        Assert.Equal("Invalid account", seller.LastError);
        Assert.Equal(1, (await env.Alerts.GetAsync()).PayoutsNeedingReview);

        var oldKey = seller.Reference;
        await env.Payouts.RetryAsync(seller.Id, false, "0881000222", MobileNetwork.Tnm.ToString(), "staff:fin");
        Assert.Equal(PayoutStatus.Queued, seller.Status);
        Assert.Equal("265881000222", seller.Msisdn);
        Assert.Equal(MobileNetwork.Tnm.ToString(), seller.Network);
        Assert.NotEqual(oldKey, seller.Reference);

        await env.Payouts.ExecuteDueAsync();
        Assert.Equal(PayoutStatus.Submitted, seller.Status);
        Assert.True(env.Db.Audit.Rows.Any(a => a.Action == "payout.retry"));
    }

    [Fact]
    public async Task A_failure_reported_by_the_provider_is_retried_under_a_new_key()
    {
        var env = await TestEnv.NewAsync();
        var e = await env.ReleasedOrderAsync();
        await env.Payouts.ExecuteDueAsync();
        var seller = env.Db.Payouts.Rows.Single(p => p.Kind == PayoutKind.Seller);
        var oldKey = seller.Reference;

        await env.Payouts.ApplyAsync(new PayoutNotification("payout.failed:1", oldKey, PayoutEventKind.Failed, seller.Amount, 0, "wallet limit", null), null);

        Assert.Equal(PayoutStatus.Queued, seller.Status);
        Assert.NotEqual(oldKey, seller.Reference);
        Assert.Equal(0, env.Db.Ledger.Rows.Count(t => t.Kind == "payout"));      // nothing cleared yet
    }

    [Fact]
    public async Task A_reversal_after_confirmation_restores_the_payable_and_flags_ops()
    {
        var env = await TestEnv.NewAsync();
        var e = await env.ReleasedOrderAsync();
        await env.ConfirmAllPayoutsAsync(e);
        Assert.Equal(EscrowState.Settled, e.State);
        var seller = env.Db.Payouts.Rows.Single(p => p.Kind == PayoutKind.Seller);

        await env.Payouts.ApplyAsync(new PayoutNotification("payout.reversed:1", seller.Reference, PayoutEventKind.Reversed, seller.Amount, 0, null, null), null);

        Assert.Equal(PayoutStatus.NeedsReview, seller.Status);
        Assert.Equal(seller.Amount, env.Db.Balance(Accounts.SellerPayable(e.SellerId)));
        Assert.Contains("reversed", e.OpsFlag);
        Assert.True((await env.Repos.GetSummaryAsync("MWK", default)).IsBalanced);
    }

    [Fact]
    public async Task Submitted_payouts_with_no_webhook_are_recovered_by_polling()
    {
        var env = await TestEnv.NewAsync();
        var e = await env.ReleasedOrderAsync();
        await env.Payouts.ExecuteDueAsync();
        env.Provider.PayoutStatus = new PayoutStatusResult(RemoteStatus.Succeeded, null, 0, null);

        Assert.Equal(0, await env.Payouts.PollSubmittedAsync());      // too soon
        env.Clock.Advance(TimeSpan.FromMinutes(3));
        Assert.Equal(2, await env.Payouts.PollSubmittedAsync());
        Assert.Equal(EscrowState.Settled, e.State);
    }

    [Fact]
    public async Task A_full_order_leaves_every_account_at_the_expected_final_balance()
    {
        var env = await TestEnv.NewAsync();
        var e = await env.PendingOrderAsync();
        await env.Collections.HandleAsync(env.Paid(e, fee: 150_000), null);
        await env.PickupAsync(e);
        await env.Handoff.ArriveAsync(env.Courier.Id, e.Id, new ArrivalCommand("arr", await env.PhotosAsync(1), new GeoPoint(-13.9, 33.7), null));
        await env.Handoff.SubmitOtpAsync(env.Courier.Id, e.Id, "otp", env.LastOtp());
        await env.ConfirmAllPayoutsAsync(e);

        Assert.Equal(EscrowState.Settled, e.State);
        var s = await env.Repos.GetSummaryAsync("MWK", default);
        Assert.Equal(0, s.HeldInEscrow);
        Assert.Equal(0, s.OwedToSellers + s.OwedToCouriers + s.OwedToBuyers);
        Assert.Equal(600_000, s.PlatformRevenue);
        Assert.Equal(150_000 + 200, s.ProviderFees);         // collection fee + two payout fees of 100 each
        Assert.Equal(600_000 - 150_200, s.ProviderBalance);
        Assert.True(s.IsBalanced);
    }
}

public class DisputeTests
{
    [Fact]
    public async Task Raising_a_dispute_freezes_the_order_and_texts_everyone()
    {
        var env = await TestEnv.NewAsync();
        var e = await env.DeliveredOrderAsync();

        var d = await env.Disputes.RaiseAsync(e.Id, "staff:ops", "Buyer says the seal was broken");

        Assert.Equal(EscrowState.Disputed, e.State);
        Assert.Equal(EscrowState.Delivered, d.PriorState);
        Assert.Equal(d.OpenedAt.AddHours(48), d.DueAt);
        Assert.Equal(3, env.Db.Notifications.Rows.Count(n => n.Template == "dispute_opened"));
        Assert.Equal(TestEnv.Total, env.Held(e));
    }

    [Fact]
    public async Task A_released_order_cannot_be_disputed()
    {
        var env = await TestEnv.NewAsync();
        var e = await env.ReleasedOrderAsync();
        await Assert.ThrowsAsync<InvalidEscrowTransitionException>(() => env.Disputes.RaiseAsync(e.Id, "staff:ops", "too late"));
    }

    [Fact]
    public async Task A_reason_is_required_to_open_and_to_decide()
    {
        var env = await TestEnv.NewAsync();
        var e = await env.DeliveredOrderAsync();
        await Assert.ThrowsAsync<DomainValidationException>(() => env.Disputes.RaiseAsync(e.Id, "staff:ops", " "));
        var d = await env.Disputes.RaiseAsync(e.Id, "staff:ops", "damaged");
        await Assert.ThrowsAsync<DomainValidationException>(() => env.Disputes.DecideAsync(d.Id, new DecideDisputeCommand(DisputeOutcome.RefundBuyerKeepDeliveryFee, " "), "staff:admin"));
    }

    [Fact]
    public async Task Refund_with_courier_fee_retained_matches_the_decision_screen()
    {
        var env = await TestEnv.NewAsync();
        var e = await env.DeliveredOrderAsync();
        var d = await env.Disputes.RaiseAsync(e.Id, "staff:ops", "Item damaged");

        await env.Disputes.DecideAsync(d.Id, new DecideDisputeCommand(DisputeOutcome.RefundBuyerKeepDeliveryFee, "Seal broken in transit, courier liable."), "staff:admin");

        Assert.Equal(EscrowState.Refunded, e.State);
        Assert.Equal(DisputeStatus.Decided, d.Status);
        Assert.Equal(30_000_000, d.BuyerAmount);     // K300,000 to the buyer
        Assert.Equal(800_000, d.CourierAmount);      // K8,000 delivery fee retained
        Assert.Equal(0, d.PlatformAmount);           // platform fee K0
        Assert.Equal(0, env.Held(e));
        Assert.Equal(2, env.Db.Payouts.Rows.Count);
        Assert.Null(e.OpsFlag);
        Assert.True(env.Db.Audit.Rows.Any(a => a.Action == "dispute.decide"));
    }

    [Fact]
    public async Task A_full_refund_decision_pays_nobody_but_the_buyer()
    {
        var env = await TestEnv.NewAsync();
        var e = await env.DeliveredOrderAsync();
        var d = await env.Disputes.RaiseAsync(e.Id, "staff:ops", "Courier lost the item");
        await env.Disputes.DecideAsync(d.Id, new DecideDisputeCommand(DisputeOutcome.RefundBuyerFull, "Courier lost it"), "staff:admin");
        var payout = Assert.Single(env.Db.Payouts.Rows);
        Assert.Equal(TestEnv.Total, payout.Amount);
        Assert.Equal(PayoutKind.BuyerRefund, payout.Kind);
    }

    [Fact]
    public async Task Deciding_for_the_seller_releases_the_money_like_a_normal_release()
    {
        var env = await TestEnv.NewAsync();
        var e = await env.DeliveredOrderAsync();
        var d = await env.Disputes.RaiseAsync(e.Id, "staff:ops", "Buyer unreachable");
        await env.Disputes.DecideAsync(d.Id, new DecideDisputeCommand(DisputeOutcome.ReleaseToSeller, "Delivery proof is solid"), "staff:admin");

        Assert.Equal(EscrowState.Released, e.State);
        Assert.Equal(29_400_000, env.Db.Payouts.Rows.Single(p => p.Kind == PayoutKind.Seller).Amount);
        Assert.Equal(600_000, (await env.Repos.GetSummaryAsync("MWK", default)).PlatformRevenue);
    }

    [Fact]
    public async Task A_split_decision_must_add_up_to_the_order_total()
    {
        var env = await TestEnv.NewAsync();
        var e = await env.DeliveredOrderAsync();
        var d = await env.Disputes.RaiseAsync(e.Id, "staff:ops", "Partly damaged");

        await Assert.ThrowsAsync<DomainValidationException>(() => env.Disputes.DecideAsync(d.Id,
            new DecideDisputeCommand(DisputeOutcome.Split, "half", BuyerAmount: 1_000_000, SellerAmount: 1_000_000), "staff:admin"));
        Assert.Equal(EscrowState.Disputed, e.State);

        await env.Disputes.DecideAsync(d.Id, new DecideDisputeCommand(DisputeOutcome.Split, "Half each after damage",
            BuyerAmount: 10_000_000, SellerAmount: 19_400_000, CourierAmount: 800_000, PlatformAmount: 600_000), "staff:admin");
        Assert.Equal(3, env.Db.Payouts.Rows.Count);
        Assert.True((await env.Repos.GetSummaryAsync("MWK", default)).IsBalanced);
    }

    [Fact]
    public async Task A_dispute_is_decided_only_once()
    {
        var env = await TestEnv.NewAsync();
        var e = await env.DeliveredOrderAsync();
        var d = await env.Disputes.RaiseAsync(e.Id, "staff:ops", "x");
        await env.Disputes.DecideAsync(d.Id, new DecideDisputeCommand(DisputeOutcome.RefundBuyerFull, "ok"), "staff:admin");
        await Assert.ThrowsAsync<DomainValidationException>(() => env.Disputes.DecideAsync(d.Id, new DecideDisputeCommand(DisputeOutcome.ReleaseToSeller, "again"), "staff:admin"));
    }
}
