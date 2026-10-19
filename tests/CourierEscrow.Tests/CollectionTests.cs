using CourierEscrow.Application.Interfaces;
using CourierEscrow.Application.Services;
using CourierEscrow.Application.Services.Support;
using CourierEscrow.Domain;
using CourierEscrow.Domain.Models;
using CourierEscrow.Domain.Ledger;
using CourierEscrow.Tests.Fakes;

namespace CourierEscrow.Tests;

public class CollectionTests
{
    [Fact]
    public async Task Starting_payment_creates_an_intent_calls_the_provider_once_and_tells_the_buyer_where_to_pay()
    {
        var env = await TestEnv.NewAsync();
        var e = await env.CreateOrderAsync();
        await env.ConfirmProductAsync(e);

        var start = await env.Collections.StartAsync(e.Id, "staff:ops");

        Assert.Equal("PaymentPending", start.State);
        Assert.Equal("7788990011", start.AccountNumber);
        var call = Assert.Single(env.Provider.Collections);
        Assert.Equal(e.PaymentReference, call.Reference);
        Assert.Equal(16, call.Reference.Length);
        Assert.Equal(TestEnv.Total, call.Amount);
        Assert.Equal("265888111222", call.BuyerPhone);
        var intent = env.Db.Intents.Rows.Single();
        Assert.Equal(PaymentIntentStatus.Pending, intent.Status);
        var attempt = Assert.Single(env.Db.PaymentAttempts.Rows);
        Assert.Equal(PaymentAttemptOutcome.Succeeded, attempt.Outcome);
        Assert.Equal(intent.Id, attempt.PaymentIntentId);
        Assert.Equal(intent.Reference, attempt.IdempotencyKey);
        Assert.NotNull(attempt.FinishedAt);
        var sms = env.Db.Notifications.Rows.Last(n => n.Template == "payment_instructions");
        Assert.Contains("K308,000", sms.Body);
        Assert.Contains("7788990011", sms.Body);
    }

    [Fact]
    public async Task Starting_twice_does_not_call_the_provider_again()
    {
        var env = await TestEnv.NewAsync();
        var e = await env.CreateOrderAsync();
        await env.ConfirmProductAsync(e);
        await env.Collections.StartAsync(e.Id, "staff:ops");
        var again = await env.Collections.StartAsync(e.Id, "staff:ops");
        Assert.Equal(1, env.Provider.Collections.Count);
        Assert.Equal(e.PaymentReference, again.Reference);
    }

    [Fact]
    public async Task A_provider_outage_leaves_the_intent_saved_and_the_retry_reuses_the_same_idempotency_key()
    {
        var env = await TestEnv.NewAsync();
        var e = await env.CreateOrderAsync();
        await env.ConfirmProductAsync(e);
        env.Provider.CollectionFails = true;
        await Assert.ThrowsAsync<ProviderException>(() => env.Collections.StartAsync(e.Id, "staff:ops"));

        var reference = e.PaymentReference;
        Assert.Equal(EscrowState.PaymentPending, e.State);
        Assert.Equal(PaymentIntentStatus.Initiated, env.Db.Intents.Rows.Single().Status);

        env.Provider.CollectionFails = false;
        await env.Collections.StartAsync(e.Id, "staff:ops");
        Assert.Equal(2, env.Provider.Collections.Count);
        Assert.Equal(reference, env.Provider.Collections[1].Reference);
        Assert.Equal(1, env.Db.Intents.Rows.Count);
        Assert.Equal(PaymentIntentStatus.Pending, env.Db.Intents.Rows.Single().Status);
        var attempts = env.Db.PaymentAttempts.Rows.OrderBy(a => a.StartedAt).ToArray();
        Assert.Equal(2, attempts.Length);
        Assert.Equal(PaymentAttemptOutcome.Failed, attempts[0].Outcome);
        Assert.Equal("provider unreachable", attempts[0].FailureMessage);
        Assert.Equal(PaymentAttemptOutcome.Succeeded, attempts[1].Outcome);
        Assert.All(attempts, attempt => Assert.Equal(reference, attempt.IdempotencyKey));
    }

    [Fact]
    public async Task An_idempotency_payload_conflict_retries_once_with_a_new_key_and_saves_both_attempts()
    {
        var env = await TestEnv.NewAsync();
        var e = await env.CreateOrderAsync();
        await env.ConfirmProductAsync(e);
        var fingerprint = new string('A', 64);
        env.Provider.CollectionFailures.Enqueue(new ProviderException(
            "The same idempotency key was used with a different request payload.", false,
            statusCode: 409, requestFingerprint: fingerprint, idempotencyConflict: true));

        await env.Collections.StartAsync(e.Id, "staff:ops");

        var attempts = env.Db.PaymentAttempts.Rows.OrderBy(attempt => attempt.StartedAt).ToArray();
        Assert.Equal(2, attempts.Length);
        Assert.Equal(PaymentAttemptOutcome.Failed, attempts[0].Outcome);
        Assert.Equal(409, attempts[0].ProviderStatusCode);
        Assert.Equal(fingerprint, attempts[0].RequestFingerprint);
        Assert.False(attempts[0].TransientFailure);
        Assert.Contains("different request payload", attempts[0].FailureMessage);
        Assert.Equal(PaymentAttemptOutcome.Succeeded, attempts[1].Outcome);
        Assert.NotEqual(attempts[0].IdempotencyKey, attempts[1].IdempotencyKey);
        Assert.StartsWith("ce-", attempts[1].IdempotencyKey);
        Assert.Equal(attempts[0].IdempotencyKey, env.Provider.Collections[0].IdempotencyKey);
        Assert.Equal(attempts[1].IdempotencyKey, env.Provider.Collections[1].IdempotencyKey);
    }

    [Fact]
    public async Task A_funded_order_cannot_start_payment_again()
    {
        var env = await TestEnv.NewAsync();
        var e = await env.FundedOrderAsync();
        await Assert.ThrowsAsync<InvalidEscrowTransitionException>(() => env.Collections.StartAsync(e.Id, "staff:ops"));
    }

    [Fact]
    public async Task Payment_funds_the_order_and_posts_the_design_example_to_the_ledger()
    {
        var env = await TestEnv.NewAsync();
        var e = await env.FundedOrderAsync();

        Assert.Equal(EscrowState.Funded, e.State);
        Assert.Equal(TestEnv.Total, env.Held(e));
        var summary = await env.Repos.GetSummaryAsync("MWK", default);
        Assert.Equal(TestEnv.Total, summary.ProviderBalance);
        Assert.Equal(TestEnv.Total, summary.HeldInEscrow);
        Assert.True(summary.IsBalanced);
        Assert.Equal(PaymentIntentStatus.Succeeded, env.Db.Intents.Rows.Single().Status);
        Assert.True(env.Db.Outbox.Rows.Any(x => x is EscrowFunded));
    }

    [Fact]
    public async Task Funding_tells_the_seller_and_the_courier()
    {
        var env = await TestEnv.NewAsync();
        await env.FundedOrderAsync();
        var seller = env.Db.Notifications.Rows.Single(n => n.Template == "funded_seller");
        Assert.Equal("265991234567", seller.ToPhone);
        Assert.Contains("Do not release without the courier", seller.Body);
        Assert.Contains("Mphatso Banda", seller.Body);
        Assert.Equal("265999000111", env.Db.Notifications.Rows.Single(n => n.Template == "funded_courier").ToPhone);
    }

    [Fact]
    public async Task The_providers_collection_fee_is_booked_as_an_expense_and_the_books_still_balance()
    {
        var env = await TestEnv.NewAsync();
        var e = await env.PendingOrderAsync();
        await env.Collections.HandleAsync(env.Paid(e, fee: 50_000), null);

        var summary = await env.Repos.GetSummaryAsync("MWK", default);
        Assert.Equal(50_000, summary.ProviderFees);
        Assert.Equal(TestEnv.Total - 50_000, summary.ProviderBalance);
        Assert.True(summary.IsBalanced);
    }

    [Fact]
    public async Task A_wrong_amount_is_flagged_and_never_funds_the_order()
    {
        var env = await TestEnv.NewAsync();
        var e = await env.PendingOrderAsync();

        var outcome = await env.Collections.HandleAsync(env.Paid(e, amount: TestEnv.Item), null);

        Assert.Equal(CallbackOutcome.AmountMismatch, outcome);
        Assert.Equal(EscrowState.PaymentPending, e.State);
        Assert.Contains("Wrong amount", e.OpsFlag);
        Assert.Equal(0, env.Db.Ledger.Rows.Count);
        Assert.Equal(PaymentIntentStatus.Mismatch, env.Db.Intents.Rows.Single().Status);
        Assert.Contains("wrong amount", env.Db.Notifications.Rows.Last().Body.ToLower());
    }

    [Fact]
    public async Task The_same_event_applied_twice_funds_only_once()
    {
        var env = await TestEnv.NewAsync();
        var e = await env.PendingOrderAsync();
        var n = env.Paid(e);

        Assert.Equal(CallbackOutcome.Processed, await env.Collections.HandleAsync(n, null));
        Assert.Equal(CallbackOutcome.Ignored, await env.Collections.HandleAsync(n, null));

        Assert.Equal(1, env.Db.Ledger.Rows.Count(t => t.Kind == "funding"));
        Assert.Equal(TestEnv.Total, env.Held(e));
    }

    [Fact]
    public async Task A_concurrent_duplicate_that_slips_past_the_checks_is_stopped_by_the_ledger_key()
    {
        var env = await TestEnv.NewAsync();
        var e = await env.PendingOrderAsync();
        var n = env.Paid(e);
        await env.Collections.HandleAsync(n, null);

        // Simulate the race: a second worker still saw the order as PaymentPending and tries to post the same funding.
        env.Db.Ledger.Add(EscrowPostings.Funding(e, $"funding:{n.EventKey}", env.Clock.GetUtcNow()));
        await Assert.ThrowsAsync<DuplicateOperationException>(() => env.Db.CommitAsync(default));
        Assert.Equal(TestEnv.Total, env.Held(e));
    }

    [Fact]
    public async Task A_payment_that_arrives_after_cancellation_is_flagged_for_a_manual_refund()
    {
        var env = await TestEnv.NewAsync();
        var e = await env.PendingOrderAsync();
        await env.Orders.CancelAsync(e.Id, "staff:ops", "buyer changed mind");

        var outcome = await env.Collections.HandleAsync(env.Paid(e), null);

        Assert.Equal(CallbackOutcome.Ignored, outcome);
        Assert.Equal(EscrowState.Cancelled, e.State);
        Assert.Contains("refund the buyer manually", e.OpsFlag);
        Assert.Equal(0, env.Db.Ledger.Rows.Count);
    }

    [Fact]
    public async Task A_provider_reversal_is_flagged()
    {
        var env = await TestEnv.NewAsync();
        var e = await env.FundedOrderAsync();
        await env.Collections.HandleAsync(new CollectionNotification("payrequest.reversed:TX1", e.PaymentReference!, CollectionEventKind.Reversed, e.Total, 0, "MWK", "TX1"), null);
        Assert.Contains("reversed", e.OpsFlag);
    }

    [Fact]
    public async Task A_failed_payment_cancels_the_order_and_tells_both_parties()
    {
        var env = await TestEnv.NewAsync();
        var e = await env.PendingOrderAsync();
        await env.Collections.HandleAsync(new CollectionNotification("fail:1", e.PaymentReference!, CollectionEventKind.Failed, 0, 0, "MWK", null), null);

        Assert.Equal(EscrowState.Cancelled, e.State);
        Assert.Equal(2, env.Db.Notifications.Rows.Count(n => n.Template == "payment_failed"));
        Assert.Equal(PaymentIntentStatus.Failed, env.Db.Intents.Rows.Single().Status);
    }

    [Fact]
    public async Task An_unknown_reference_is_recorded_not_acted_on()
    {
        var env = await TestEnv.NewAsync();
        var outcome = await env.Collections.HandleAsync(new CollectionNotification("x:1", "ESDOESNOTEXIST00", CollectionEventKind.Succeeded, 100, 0, "MWK", null), null);
        Assert.Equal(CallbackOutcome.UnknownReference, outcome);
        Assert.Equal(0, env.Db.Ledger.Rows.Count);
    }

    [Fact]
    public async Task Unpaid_orders_are_cancelled_when_the_window_passes()
    {
        var env = await TestEnv.NewAsync();
        var e = await env.PendingOrderAsync();

        env.Clock.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(0, await env.Collections.ExpireDueAsync());
        Assert.Equal(EscrowState.PaymentPending, e.State);

        env.Clock.Advance(TimeSpan.FromMinutes(6));
        Assert.Equal(1, await env.Collections.ExpireDueAsync());
        Assert.Equal(EscrowState.Cancelled, e.State);
        Assert.Equal(PaymentIntentStatus.Expired, env.Db.Intents.Rows.Single().Status);
        Assert.Equal(2, env.Db.Notifications.Rows.Count(n => n.Template == "payment_expired"));
    }

    [Fact]
    public async Task A_lost_webhook_is_recovered_by_polling_the_provider()
    {
        var env = await TestEnv.NewAsync();
        var e = await env.PendingOrderAsync();
        env.Provider.CollectionStatus = new CollectionStatusResult(RemoteStatus.Succeeded, TestEnv.Total, 0);

        Assert.Equal(0, await env.Collections.PollPendingAsync()); // too soon
        env.Clock.Advance(TimeSpan.FromMinutes(3));
        Assert.Equal(1, await env.Collections.PollPendingAsync());

        Assert.Equal(EscrowState.Funded, e.State);
        Assert.Equal(TestEnv.Total, env.Held(e));
    }

    [Fact]
    public async Task Polling_with_no_answer_changes_nothing()
    {
        var env = await TestEnv.NewAsync();
        var e = await env.PendingOrderAsync();
        env.Clock.Advance(TimeSpan.FromMinutes(3));
        await env.Collections.PollPendingAsync();
        Assert.Equal(EscrowState.PaymentPending, e.State);
    }
}
