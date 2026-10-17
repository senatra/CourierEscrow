using CourierEscrow.Application;
using CourierEscrow.Application.Services;
using CourierEscrow.Domain;
using CourierEscrow.Tests.Fakes;

namespace CourierEscrow.Tests;

/// <summary>The buyer must confirm the product photos before payment is requested, and approving must never strand an order.</summary>
public class ApprovalTests
{
    [Fact]
    public async Task Payment_cannot_be_requested_until_the_buyer_confirms_the_photos()
    {
        var env = await TestEnv.NewAsync();
        var e = await env.CreateOrderAsync();

        await Assert.ThrowsAsync<DomainValidationException>(() => env.Collections.StartAsync(e.Id, "staff:ops"));

        Assert.Equal(EscrowState.Created, e.State);
        Assert.Empty(env.Provider.Collections);                       // the provider was never called
        Assert.Empty(env.Db.Intents.Rows);
    }

    [Fact]
    public async Task The_tracking_view_asks_for_confirmation_and_shows_the_three_photos_first()
    {
        var env = await TestEnv.NewAsync();
        var e = await env.CreateOrderAsync();

        var v = await env.Orders.GetTrackingAsync(e.TrackingToken);

        Assert.True(v.CanConfirmProduct);
        Assert.False(v.ProductConfirmed);
        Assert.Equal(3, v.Photos.Count(p => p.Kind == "Listing"));
        Assert.Null(v.PayToAccount);                                  // no payment details until they approve
        Assert.Equal("Confirm the item photos", v.Steps.First().Label);
        Assert.True(v.Steps.First().Current);
        Assert.Equal("Chikondi Phones", v.SellerName);
        Assert.Equal(TestEnv.Item, v.ItemAmount);
    }

    [Fact]
    public async Task Approving_confirms_the_photos_and_requests_payment_in_one_step()
    {
        var env = await TestEnv.NewAsync();
        var e = await env.CreateOrderAsync();

        var r = await env.Approval.ApproveAsync(e.TrackingToken);

        Assert.False(r.AlreadyConfirmed);
        Assert.True(r.PaymentReady);
        Assert.Equal("PaymentPending", r.State);
        Assert.Equal("7788990011", r.AccountNumber);
        Assert.NotNull(e.ProductConfirmedAt);
        Assert.Equal(1, env.Provider.Collections.Count);

        var v = await env.Orders.GetTrackingAsync(e.TrackingToken);
        Assert.True(v.ProductConfirmed);
        Assert.False(v.CanConfirmProduct);
        Assert.Equal("7788990011", v.PayToAccount);
    }

    [Fact]
    public async Task Approving_twice_does_not_charge_or_call_the_provider_twice()
    {
        var env = await TestEnv.NewAsync();
        var e = await env.CreateOrderAsync();

        var first = await env.Approval.ApproveAsync(e.TrackingToken);
        var second = await env.Approval.ApproveAsync(e.TrackingToken);

        Assert.True(second.AlreadyConfirmed);
        Assert.True(second.PaymentReady);
        Assert.Equal(first.Reference, second.Reference);
        Assert.Equal(1, env.Provider.Collections.Count);
        Assert.Equal(1, env.Db.Intents.Rows.Count);
    }

    [Fact]
    public async Task A_provider_outage_keeps_the_approval_and_the_next_tap_resumes_the_payment()
    {
        var env = await TestEnv.NewAsync();
        var e = await env.CreateOrderAsync();
        env.Provider.CollectionFails = true;

        var failed = await env.Approval.ApproveAsync(e.TrackingToken);

        Assert.False(failed.PaymentReady);                            // reported, not thrown: the page can show a retry button
        Assert.NotNull(failed.Message);
        Assert.Equal("PaymentPending", failed.State);
        Assert.NotNull(e.ProductConfirmedAt);                         // the approval was not lost

        env.Provider.CollectionFails = false;
        var retry = await env.Approval.ApproveAsync(e.TrackingToken);

        Assert.True(retry.AlreadyConfirmed);
        Assert.True(retry.PaymentReady);
        Assert.Equal("7788990011", retry.AccountNumber);
        Assert.Equal(2, env.Provider.Collections.Count);
        Assert.Equal(env.Provider.Collections[0].Reference, env.Provider.Collections[1].Reference);   // same idempotency key
        Assert.Equal(1, env.Db.Intents.Rows.Count);
    }

    [Fact]
    public async Task Declining_flags_ops_starts_no_payment_and_the_buyer_can_still_approve_later()
    {
        var env = await TestEnv.NewAsync();
        var e = await env.CreateOrderAsync();

        await env.Orders.DeclineProductAsync(e.TrackingToken, "The phone in the photos is black, I ordered white");

        var v = await env.Orders.GetTrackingAsync(e.TrackingToken);
        Assert.True(v.ProductDeclined);
        Assert.True(v.CanConfirmProduct);
        Assert.Empty(env.Provider.Collections);
        Assert.Contains("black", e.OpsFlag);

        await Assert.ThrowsAsync<DomainValidationException>(() => env.Orders.DeclineProductAsync(e.TrackingToken, " "));

        var r = await env.Approval.ApproveAsync(e.TrackingToken);
        Assert.True(r.PaymentReady);
        Assert.Null(e.OpsFlag);                                       // the buyer's complaint is cleared once they approve
        Assert.False((await env.Orders.GetTrackingAsync(e.TrackingToken)).ProductDeclined);
    }

    [Fact]
    public async Task A_cancelled_order_cannot_be_approved_and_an_approved_one_cannot_be_declined()
    {
        var env = await TestEnv.NewAsync();
        var cancelled = await env.CreateOrderAsync();
        await env.Orders.CancelAsync(cancelled.Id, "staff:ops", "buyer changed mind");
        await Assert.ThrowsAsync<InvalidEscrowTransitionException>(() => env.Approval.ApproveAsync(cancelled.TrackingToken));

        var approved = await env.CreateOrderAsync();
        await env.Approval.ApproveAsync(approved.TrackingToken);
        await Assert.ThrowsAsync<DomainValidationException>(() => env.Orders.DeclineProductAsync(approved.TrackingToken, "changed my mind"));
    }

    [Fact]
    public async Task Approval_is_recorded_in_the_order_timeline_before_the_payment_request()
    {
        var env = await TestEnv.NewAsync();
        var e = await env.CreateOrderAsync();
        await env.Approval.ApproveAsync(e.TrackingToken);

        var types = env.Db.OrderEvents.Rows.Where(x => x.EscrowId == e.Id).Select(x => x.Type).ToList();
        Assert.Equal("Created,ProductConfirmed,PaymentRequested", string.Join(",", types));
    }

    [Fact]
    public async Task Approve_then_pay_funds_the_order_and_the_money_is_held()
    {
        var env = await TestEnv.NewAsync();
        var e = await env.CreateOrderAsync();
        await env.Approval.ApproveAsync(e.TrackingToken);

        var outcome = await env.Collections.HandleAsync(env.Paid(e), null);

        Assert.Equal(CallbackOutcome.Processed, outcome);
        Assert.Equal(EscrowState.Funded, e.State);
        Assert.Equal(TestEnv.Total, env.Held(e));
    }
}

/// <summary>The development walk-through (pickup, arrival, accept) must behave like the real courier flow.</summary>
public class DevSimulationTests
{
    [Fact]
    public async Task A_whole_order_can_be_walked_from_approval_to_settled_with_the_dev_steps()
    {
        var env = await TestEnv.NewAsync();
        var e = await env.CreateOrderAsync();
        await env.Approval.ApproveAsync(e.TrackingToken);
        await env.Collections.HandleAsync(env.Paid(e), null);

        var pickup = await env.Sim.PickupAsync(e.TrackingToken);
        Assert.Equal("PickedUp", pickup.State);
        Assert.StartsWith("SEAL-", pickup.SealId!);
        Assert.Equal(1, env.Db.Otps.Rows.Count);
        Assert.True(env.Db.Notifications.Rows.Any(n => n.Template == "picked_up_buyer" && n.Sensitive));   // the buyer's code is texted

        Assert.Equal("Delivered", (await env.Sim.ArriveAsync(e.TrackingToken)).State);
        Assert.Equal("Released", (await env.Sim.AcceptAsync(e.TrackingToken)).State);

        Assert.Equal(0, env.Held(e));
        Assert.Equal(2, env.Db.Payouts.Rows.Count(p => p.EscrowId == e.Id));
        Assert.Equal(OtpStatus.Used, env.Db.Otps.Rows.Single().Status);

        await env.ConfirmAllPayoutsAsync(e);
        Assert.Equal(EscrowState.Settled, (await env.GetAsync(e)).State);

        var types = env.Db.OrderEvents.Rows.Where(x => x.EscrowId == e.Id).Select(x => x.Type).ToList();
        Assert.Equal("Created,ProductConfirmed,PaymentRequested,PaymentConfirmed,PickedUp,Arrived,OtpAccepted,Settled", string.Join(",", types));
    }

    [Fact]
    public async Task The_dev_steps_refuse_to_run_out_of_order()
    {
        var env = await TestEnv.NewAsync();
        var e = await env.CreateOrderAsync();

        await Assert.ThrowsAsync<DomainValidationException>(() => env.Sim.PickupAsync(e.TrackingToken));   // not paid
        await env.Approval.ApproveAsync(e.TrackingToken);
        await Assert.ThrowsAsync<DomainValidationException>(() => env.Sim.PickupAsync(e.TrackingToken));   // still not paid
        await env.Collections.HandleAsync(env.Paid(e), null);
        await Assert.ThrowsAsync<DomainValidationException>(() => env.Sim.ArriveAsync(e.TrackingToken));   // not picked up
        await Assert.ThrowsAsync<DomainValidationException>(() => env.Sim.AcceptAsync(e.TrackingToken));   // not arrived
        await Assert.ThrowsAsync<NotFoundException>(() => env.Sim.PickupAsync("nope"));
    }

    private static async Task<(TestEnv env, Escrow e)> AtTheDoorAsync()
    {
        var env = await TestEnv.NewAsync();
        var e = await env.CreateOrderAsync();
        await env.Approval.ApproveAsync(e.TrackingToken);
        await env.Collections.HandleAsync(env.Paid(e), null);
        await env.Sim.PickupAsync(e.TrackingToken);
        await env.Sim.ArriveAsync(e.TrackingToken);
        return (env, e);
    }

    [Fact]
    public async Task The_courier_typing_the_real_code_releases_the_money_and_the_payouts_settle()
    {
        var (env, e) = await AtTheDoorAsync();
        var code = env.LastOtp();

        var r = await env.Sim.EnterCodeAsync(e.TrackingToken, code, "t-1");

        Assert.Equal("Accepted", r.Result);
        Assert.Equal("Released", r.State);
        Assert.Equal(0, env.Held(e));
        Assert.Equal(2, env.Db.Payouts.Rows.Count(p => p.EscrowId == e.Id));   // seller and courier
        var accepted = env.Db.OrderEvents.Rows.Single(x => x.EscrowId == e.Id && x.Type == "OtpAccepted");
        Assert.StartsWith("courier:", accepted.Actor);

        var status = await env.Sim.PayOutAsync(e.TrackingToken);
        Assert.Equal("Settled", status.State);
        Assert.All(status.Payouts, p => Assert.Equal("Confirmed", p.Status));
    }

    [Fact]
    public async Task A_wrong_code_is_counted_and_moves_no_money()
    {
        var (env, e) = await AtTheDoorAsync();
        var real = env.LastOtp();
        var wrong = real == "000000" ? "111111" : "000000";

        var r = await env.Sim.EnterCodeAsync(e.TrackingToken, wrong, "w1");

        Assert.Equal("Wrong", r.Result);
        Assert.Equal(4, r.AttemptsLeft);
        Assert.Equal("Delivered", r.State);
        Assert.Equal(TestEnv.Total, env.Held(e));
        Assert.Empty(env.Db.Payouts.Rows.Where(p => p.EscrowId == e.Id));
        Assert.Equal(4, (await env.Sim.StatusAsync(e.TrackingToken)).AttemptsLeft);
    }

    [Fact]
    public async Task Five_wrong_codes_lock_the_order_and_even_the_right_code_then_fails()
    {
        var (env, e) = await AtTheDoorAsync();
        var real = env.LastOtp();
        var wrong = real == "000000" ? "111111" : "000000";

        for (var i = 0; i < 4; i++) Assert.Equal("Wrong", (await env.Sim.EnterCodeAsync(e.TrackingToken, wrong, "w" + i)).Result);
        Assert.Equal("Locked", (await env.Sim.EnterCodeAsync(e.TrackingToken, wrong, "w4")).Result);

        Assert.Equal("Locked", (await env.Sim.EnterCodeAsync(e.TrackingToken, real, "late")).Result);
        Assert.Equal(TestEnv.Total, env.Held(e));
        Assert.True((await env.Sim.StatusAsync(e.TrackingToken)).CodeLocked);
    }

    [Fact]
    public async Task The_same_click_sent_twice_is_not_counted_twice()
    {
        var (env, e) = await AtTheDoorAsync();
        var real = env.LastOtp();
        var wrong = real == "000000" ? "111111" : "000000";

        await env.Sim.EnterCodeAsync(e.TrackingToken, wrong, "same-click");
        var again = await env.Sim.EnterCodeAsync(e.TrackingToken, wrong, "same-click");

        Assert.True(again.Replayed);
        Assert.Equal(4, (await env.Sim.StatusAsync(e.TrackingToken)).AttemptsLeft);
    }

    [Fact]
    public async Task The_code_screen_and_the_payout_step_refuse_to_run_early()
    {
        var env = await TestEnv.NewAsync();
        var e = await env.CreateOrderAsync();
        await env.Approval.ApproveAsync(e.TrackingToken);
        await env.Collections.HandleAsync(env.Paid(e), null);

        await Assert.ThrowsAsync<DomainValidationException>(() => env.Sim.PayOutAsync(e.TrackingToken));                    // not released
        await Assert.ThrowsAsync<InvalidEscrowTransitionException>(() => env.Sim.EnterCodeAsync(e.TrackingToken, "123456", "x"));   // not at the door
        await Assert.ThrowsAsync<DomainValidationException>(() => env.Sim.EnterCodeAsync(e.TrackingToken, "  ", "z"));             // empty
    }
}
