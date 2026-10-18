using CourierEscrow.Application.Helpers;
using CourierEscrow.Application.Interfaces;
using CourierEscrow.Application.Services;
using CourierEscrow.Application.Services.Support;
using CourierEscrow.Domain;
using CourierEscrow.Domain.Models;
using CourierEscrow.Domain.Ledger;
using CourierEscrow.Tests.Fakes;

namespace CourierEscrow.Tests;

public class OrderServiceTests
{
    [Fact]
    public async Task Money_is_formatted_the_way_people_read_it()
    {
        Assert.Equal("K308,000", Money.Format(30_800_000));
        Assert.Equal("K1,500.50", Money.Format(150_050));
        Assert.Equal("USD 5", Money.Format(500, "USD"));
        Assert.Equal(10_000m, Money.ToMajor(1_000_000));
        Assert.Equal(850_000, Money.ToMinor(8_500.00m));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Creating_an_order_stores_the_terms_makes_the_parties_and_texts_both_sides()
    {
        var env = await TestEnv.NewAsync();
        var e = await env.CreateOrderAsync();

        Assert.Equal("NX-1001", e.OrderNumber);
        Assert.Equal(EscrowState.Created, e.State);
        Assert.Equal(TestEnv.Total, e.Total);
        Assert.Equal(TestEnv.Fee, e.PlatformFee);
        Assert.Equal(200, e.FeeBps);
        Assert.Equal(2, env.Db.Parties.Rows.Count);
        var buyerSms = env.Db.Notifications.Rows.Single(n => n.Template == "order_created_buyer");
        Assert.Equal("265888111222", buyerSms.ToPhone);
        Assert.Contains("K300,000 + K8,000 delivery", buyerSms.Body);
        Assert.Contains("Chikondi Phones", buyerSms.Body);
        Assert.True(env.Db.Audit.Rows.Any(a => a.Action == "order.create"));
        Assert.True(env.Db.Outbox.Rows.Any(x => x is EscrowCreated));
    }

    [Fact]
    public async Task The_fee_rate_is_frozen_on_the_order_so_later_config_changes_do_not_touch_it()
    {
        var env = await TestEnv.NewAsync();
        var first = await env.CreateOrderAsync();
        env.Options.PlatformFeeBps = 500;
        var second = await env.CreateOrderAsync();
        Assert.Equal(200, first.FeeBps);
        Assert.Equal(TestEnv.Fee, first.PlatformFee);
        Assert.Equal(500, second.FeeBps);
        Assert.Equal("NX-1002", second.OrderNumber);
    }

    [Fact]
    public async Task Returning_customers_reuse_their_party_record()
    {
        var env = await TestEnv.NewAsync();
        await env.CreateOrderAsync();
        await env.CreateOrderAsync();
        Assert.Equal(2, env.Db.Parties.Rows.Count);
        Assert.Equal(2, env.Db.Escrows.Rows.Count);
    }

    [Fact]
    public async Task Bad_orders_are_refused_before_anything_is_saved()
    {
        var env = await TestEnv.NewAsync();
        CreateOrderCommand Cmd(string buyer = "0888111222", Guid? courier = null, long item = 100_000) =>
            new("B", buyer, null, "S", "0991234567", null, courier ?? env.Courier.Id, "x", item, 0);

        await Assert.ThrowsAsync<DomainValidationException>(() => env.Orders.CreateAsync(Cmd(buyer: "0991234567"), "staff"));   // same phone as seller
        await Assert.ThrowsAsync<DomainValidationException>(() => env.Orders.CreateAsync(Cmd(buyer: "123"), "staff"));
        await Assert.ThrowsAsync<DomainValidationException>(() => env.Orders.CreateAsync(Cmd(item: 0), "staff"));
        await Assert.ThrowsAsync<NotFoundException>(() => env.Orders.CreateAsync(Cmd(courier: Guid.NewGuid()), "staff"));
        Assert.Equal(0, env.Db.Escrows.Rows.Count);
    }

    [Fact]
    public async Task Cancelling_before_payment_closes_the_intent_and_tells_the_parties()
    {
        var env = await TestEnv.NewAsync();
        var e = await env.PendingOrderAsync();
        await env.Orders.CancelAsync(e.Id, "staff:ops", "buyer changed mind");

        Assert.Equal(EscrowState.Cancelled, e.State);
        Assert.Equal(PaymentIntentStatus.Expired, env.Db.Intents.Rows.Single().Status);
        Assert.Equal(2, env.Db.Notifications.Rows.Count(n => n.Template == "order_cancelled"));
        await Assert.ThrowsAsync<InvalidEscrowTransitionException>(() => env.Orders.CancelAsync((env.FundedOrderAsync().Result).Id, "staff:ops", "late"));
    }

    [Fact]
    public async Task A_refund_needs_a_reason_and_the_right_state()
    {
        var env = await TestEnv.NewAsync();
        var funded = await env.FundedOrderAsync();
        await Assert.ThrowsAsync<DomainValidationException>(() => env.Orders.RefundAsync(funded.Id, "staff", " "));
        var delivered = await env.DeliveredOrderAsync();
        await Assert.ThrowsAsync<InvalidEscrowTransitionException>(() => env.Orders.RefundAsync(delivered.Id, "staff", "no"));
    }

    [Fact]
    public async Task The_tracking_link_shows_progress_without_exposing_phone_numbers()
    {
        var env = await TestEnv.NewAsync();
        var e = await env.PickedUpOrderAsync();

        var view = await env.Orders.GetTrackingAsync(e.TrackingToken);

        Assert.Equal("NX-1001", view.OrderNumber);
        Assert.Equal("PickedUp", view.State);
        Assert.Equal("SL-558201", view.SealId);
        Assert.Equal("Mphatso Banda", view.CourierName);
        Assert.True(view.Steps.Single(s => s.Label.StartsWith("Picked up")).Done);
        Assert.False(view.Steps.Single(s => s.Label == "Seller paid").Done);
        Assert.False(view.GetType().GetProperties().Any(p => p.Name.Contains("Phone")));
        await Assert.ThrowsAsync<NotFoundException>(() => env.Orders.GetTrackingAsync("nope"));
    }

    [Fact]
    public async Task The_timeline_gathers_events_proofs_ledger_payouts_and_the_otp_state()
    {
        var env = await TestEnv.NewAsync();
        var e = await env.ReleasedOrderAsync();
        await env.Payouts.ExecuteDueAsync();

        var d = await env.Orders.GetDetailAsync(e.Id);

        Assert.Equal("Chikondi Phones", d.Seller.Name);
        Assert.Equal("Created,ProductConfirmed,PaymentRequested,PaymentConfirmed,PickedUp,Arrived,OtpAccepted", string.Join(",", d.Events.Select(x => x.Type)));
        Assert.Equal(7, d.Proofs.Count);   // 3 listing photos + 3 pickup + 1 arrival
        Assert.Equal(3, d.Proofs.Count(p => p.Kind == ProofKind.Listing));
        Assert.Equal(2, d.Ledger.Count);
        Assert.Equal(2, d.Payouts.Count);
        Assert.Equal(OtpStatus.Used, d.Otp!.Status);
    }

    [Fact]
    public async Task The_order_list_can_be_filtered_by_state()
    {
        var env = await TestEnv.NewAsync();
        await env.CreateOrderAsync();
        await env.FundedOrderAsync();
        Assert.Equal(2, (await env.Orders.ListAsync()).Count);
        Assert.Equal(1, (await env.Orders.ListAsync(state: EscrowState.Funded)).Count);
    }
}

public class NotificationTests
{
    [Fact]
    public async Task Queued_messages_are_sent_and_the_otp_is_decrypted_only_for_the_send_then_blanked()
    {
        var env = await TestEnv.NewAsync();
        await env.PickedUpOrderAsync();
        var code = env.LastOtp();

        var sent = await env.Dispatcher.DispatchDueAsync();

        Assert.True(sent > 0);
        Assert.True(env.Sms.Sent.Any(s => s.Body.Contains(code[..3] + " " + code[3..])));
        var otpRow = env.Db.Notifications.Rows.Single(n => n.Sensitive);
        Assert.Equal(NotificationStatus.Sent, otpRow.Status);
        Assert.Equal("[redacted]", otpRow.Body);
        Assert.Equal(0, await env.Dispatcher.DispatchDueAsync());       // nothing left
    }

    [Fact]
    public async Task A_failed_send_is_retried_with_backoff_and_then_given_up_on()
    {
        var env = await TestEnv.NewAsync();
        await env.CreateOrderAsync();
        env.Sms.Fail = true;

        await env.Dispatcher.DispatchDueAsync();
        var n = env.Db.Notifications.Rows.First();
        Assert.Equal(NotificationStatus.Pending, n.Status);
        Assert.Equal(1, n.Attempts);
        Assert.Equal(0, await env.Dispatcher.DispatchDueAsync());       // not due yet

        for (var i = 0; i < Notification.MaxAttempts; i++)
        {
            env.Clock.Advance(TimeSpan.FromHours(1));
            await env.Dispatcher.DispatchDueAsync();
        }
        Assert.Equal(NotificationStatus.Dead, n.Status);

        env.Sms.Fail = false;
        env.Clock.Advance(TimeSpan.FromHours(1));
        await env.Dispatcher.DispatchDueAsync();
        Assert.Equal(NotificationStatus.Dead, n.Status);               // dead stays dead
    }
}

public class ReconciliationTests
{
    private static readonly DateOnly Day = DateOnly.FromDateTime(TestEnv.Start.UtcDateTime);

    [Fact]
    public async Task A_clean_day_has_no_exceptions()
    {
        var env = await TestEnv.NewAsync();
        var e = await env.ReleasedOrderAsync();
        await env.Payouts.ExecuteDueAsync();
        foreach (var p in env.Db.Payouts.Rows)
            env.Provider.Remote.Add(new ProviderPayoutRecord(p.ProviderRef!, null, p.Amount, RemoteStatus.Pending));

        var r = await env.Recon.RunAsync(Day);

        Assert.Equal(0, r.Run.ExceptionCount);
        Assert.Equal(2, r.Run.MatchedCount);
        Assert.Equal(1, (await env.Recon.ListRunsAsync()).Count);
    }

    [Fact]
    public async Task Differences_between_us_and_the_provider_are_flagged_in_both_directions()
    {
        var env = await TestEnv.NewAsync();
        await env.ReleasedOrderAsync();
        await env.Payouts.ExecuteDueAsync();
        var seller = env.Db.Payouts.Rows.Single(p => p.Kind == PayoutKind.Seller);
        var courier = env.Db.Payouts.Rows.Single(p => p.Kind == PayoutKind.Courier);

        env.Provider.Remote.Add(new ProviderPayoutRecord(seller.ProviderRef!, null, seller.Amount - 5, RemoteStatus.Succeeded));   // amount differs
        env.Provider.Remote.Add(new ProviderPayoutRecord("MYSTERY-TX", null, 123_400, RemoteStatus.Succeeded));                      // not ours
        // the courier payout is missing at the provider

        var r = await env.Recon.RunAsync(Day);

        var kinds = r.Items.Select(i => i.Kind).ToList();
        Assert.Contains(ReconciliationKind.Mismatch, kinds);
        Assert.Contains(ReconciliationKind.ProviderOnly, kinds);
        Assert.Contains(ReconciliationKind.InternalOnly, kinds);
        Assert.Equal(courier.Reference, r.Items.Single(i => i.Kind == ReconciliationKind.InternalOnly).Reference);
        Assert.Equal(3, r.Run.ExceptionCount);
        Assert.Equal(3, (await env.Alerts.GetAsync()).LastReconciliationExceptions);
    }

    [Fact]
    public async Task Provider_success_confirms_the_payout_and_posts_the_ledger_without_a_webhook()
    {
        var env = await TestEnv.NewAsync();
        var escrow = await env.ReleasedOrderAsync();
        await env.Payouts.ExecuteDueAsync();
        foreach (var p in env.Db.Payouts.Rows) env.Provider.Remote.Add(new ProviderPayoutRecord(p.ProviderRef!, p.Reference, p.Amount, RemoteStatus.Succeeded, p.Kind == PayoutKind.Seller ? 125 : 0, "MWK"));

        var r = await env.Recon.RunAsync(Day);
        Assert.Equal(0, r.Run.ExceptionCount);
        Assert.Equal(2, r.Run.MatchedCount);
        Assert.All(env.Db.Payouts.Rows, p => Assert.Equal(PayoutStatus.Confirmed, p.Status));
        Assert.Equal(125, env.Db.Payouts.Rows.Single(p => p.Kind == PayoutKind.Seller).Fee);
        Assert.Equal(EscrowState.Settled, (await env.Orders.GetAsync(escrow.Id)).State);
        Assert.True((await env.Recon.RunAsync(Day)).Items.Count == 0);
    }

    [Fact]
    public async Task Provider_failure_is_flagged_for_review_before_a_manual_retry()
    {
        var env = await TestEnv.NewAsync();
        await env.ReleasedOrderAsync();
        await env.Payouts.ExecuteDueAsync();
        var payout = env.Db.Payouts.Rows.First();
        var oldReference = payout.Reference;
        env.Provider.Remote.Add(new ProviderPayoutRecord(payout.ProviderRef!, oldReference, payout.Amount, RemoteStatus.Failed));

        var result = await env.Recon.RunAsync(Day);

        Assert.Equal(PayoutStatus.NeedsReview, payout.Status);
        Assert.Equal(oldReference, payout.Reference);
        Assert.Contains("review before retry", payout.LastError!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(result.Items, i => i.Kind == ReconciliationKind.Mismatch && i.Reference == oldReference);
    }

    [Fact]
    public async Task Provider_reversal_reverses_the_confirmed_payout_and_flags_the_order()
    {
        var env = await TestEnv.NewAsync();
        var escrow = await env.ReleasedOrderAsync();
        await env.Payouts.ExecuteDueAsync();
        foreach (var payout in env.Db.Payouts.Rows)
            env.Provider.Remote.Add(new ProviderPayoutRecord(payout.ProviderRef!, payout.Reference, payout.Amount, RemoteStatus.Succeeded));
        await env.Recon.RunAsync(Day);

        env.Provider.Remote.Clear();
        var reversed = env.Db.Payouts.Rows.First();
        env.Provider.Remote.Add(new ProviderPayoutRecord(reversed.ProviderRef!, reversed.Reference, reversed.Amount, RemoteStatus.Reversed));
        foreach (var payout in env.Db.Payouts.Rows.Where(p => p.Id != reversed.Id))
            env.Provider.Remote.Add(new ProviderPayoutRecord(payout.ProviderRef!, payout.Reference, payout.Amount, RemoteStatus.Succeeded));

        var result = await env.Recon.RunAsync(Day);

        Assert.Equal(PayoutStatus.NeedsReview, reversed.Status);
        Assert.Contains(result.Items, i => i.Kind == ReconciliationKind.Mismatch && i.Reference == reversed.Reference);
        Assert.Contains(env.Db.Ledger.Rows, t => t.Kind == "payout_reversal");
        Assert.NotNull((await env.Orders.GetAsync(escrow.Id)).OpsFlag);
    }

    [Fact]
    public async Task Unknown_provider_payout_status_is_not_treated_as_a_failure()
    {
        var env = await TestEnv.NewAsync();
        await env.ReleasedOrderAsync();
        await env.Payouts.ExecuteDueAsync();
        var payout = env.Db.Payouts.Rows.First();
        env.Provider.Remote.Add(new ProviderPayoutRecord(payout.ProviderRef!, payout.Reference, payout.Amount, RemoteStatus.Unknown));

        await env.Recon.RunAsync(Day);

        Assert.Equal(PayoutStatus.NeedsReview, payout.Status);
        Assert.Contains("unrecognized", payout.LastError!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Provider_without_payout_inquiry_does_not_create_false_internal_only_exceptions()
    {
        var env = await TestEnv.NewAsync();
        await env.ReleasedOrderAsync();
        await env.Payouts.ExecuteDueAsync();
        env.Provider.SupportsPayoutInquiry = false;

        var result = await env.Recon.RunAsync(Day);

        Assert.Equal(0, result.Run.ProviderCount);
        Assert.DoesNotContain(result.Items, i => i.Kind == ReconciliationKind.InternalOnly);
        Assert.Contains(result.Items, i => i.Reference == "provider" && i.Detail.Contains("inquiry", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task The_clearing_balance_is_compared_with_the_providers_reported_balance()
    {
        var env = await TestEnv.NewAsync();
        await env.FundedOrderAsync();

        env.Provider.Balance = TestEnv.Total;
        Assert.Equal(0, (await env.Recon.RunAsync(Day)).Run.ExceptionCount);

        env.Provider.Balance = TestEnv.Total - 1;
        Assert.True((await env.Recon.RunAsync(Day)).Items.Any(i => i.Kind == ReconciliationKind.ClearingBalance));
    }

    [Fact]
    public async Task An_order_that_holds_the_wrong_amount_is_caught()
    {
        var env = await TestEnv.NewAsync();
        var e = await env.FundedOrderAsync();

        // someone posted a stray movement out of the escrow
        env.Db.Ledger.Add(LedgerTransaction.Create(e.Id, "manual", "manual:1", TestEnv.Start,
        [
            new LedgerEntry(Accounts.Escrow(e.Id), Direction.Debit, 1_000, "MWK"),
            new LedgerEntry(Accounts.PlatformRevenue, Direction.Credit, 1_000, "MWK")
        ]));
        await env.Db.CommitAsync(default);

        var r = await env.Recon.RunAsync(Day);
        Assert.True(r.Items.Any(i => i.Kind == ReconciliationKind.EscrowBalance && i.Reference == e.OrderNumber));
    }
}

public class AlertTests
{
    [Fact]
    public async Task A_quiet_system_has_nothing_to_report()
    {
        var env = await TestEnv.NewAsync();
        await env.FundedOrderAsync();
        var a = await env.Alerts.GetAsync();
        Assert.True(a.AllClear);
    }

    [Fact]
    public async Task Problems_that_need_a_person_show_up()
    {
        var env = await TestEnv.NewAsync();
        var stuck = await env.PendingOrderAsync();                            // payment will never arrive
        var delivered = await env.DeliveredOrderAsync();
        await env.Disputes.RaiseAsync(delivered.Id, "staff:ops", "damaged");

        env.Clock.Advance(TimeSpan.FromHours(49));                            // past every deadline

        var a = await env.Alerts.GetAsync();
        Assert.Equal(1, a.DisputesOverdue);
        Assert.True(a.PaymentsStuck >= 1);
        Assert.True(a.OrdersFlagged >= 1);
        Assert.False(a.AllClear);
    }
}
