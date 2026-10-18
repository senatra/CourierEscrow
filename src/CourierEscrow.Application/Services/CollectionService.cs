using CourierEscrow.Application.Helpers;
using CourierEscrow.Application.Interfaces;
using CourierEscrow.Application.Services.Support;
using CourierEscrow.Domain;
using CourierEscrow.Domain.Models;
using CourierEscrow.Domain.Ledger;

namespace CourierEscrow.Application.Services;

public enum CallbackOutcome
{
    /// <summary>The order was funded and the ledger updated (or a failure/expiry was applied).</summary>
    Processed,
    /// <summary>This provider event was already handled; nothing changed.</summary>
    Duplicate,
    /// <summary>No order uses this payment reference.</summary>
    UnknownReference,
    /// <summary>Recorded, but no state change (the order was no longer waiting for payment). May be flagged for ops.</summary>
    Ignored,
    /// <summary>The provider collected a different amount or currency. NOT funded; flagged for ops.</summary>
    AmountMismatch
}

public sealed record CollectionStart(string Reference, string? AccountNumber, DateTimeOffset ExpiresAt, string State);

/// <summary>Collecting the buyer's money: start a payment request, apply the provider's answer, poll, and expire.</summary>
public sealed class CollectionService(
    IEscrowRepository escrows, IPartyRepository parties, ICourierRepository couriers, IPaymentIntentRepository intents,
    ILedgerStore ledger, IPaymentProvider provider, EscrowCommit commit, Notifier notifier, TimeProvider clock, EscrowOptions options)
{
    private static readonly TimeSpan PollEvery = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Starts (or safely repeats) payment. The intent is committed BEFORE the provider is called and carries the
    /// idempotency reference, so a crash between "call" and "save" is repaired by calling again with the same key.
    /// </summary>
    public async Task<CollectionStart> StartAsync(Guid escrowId, string actor, CancellationToken ct = default)
    {
        var escrow = await escrows.GetAsync(escrowId, ct) ?? throw new NotFoundException($"Order {escrowId} was not found.");
        var now = clock.GetUtcNow();
        PaymentIntent intent;

        if (escrow.State == EscrowState.Created)
        {
            intent = PaymentIntent.Create(escrow, References.New("ES"), now.AddMinutes(options.PaymentWindowMinutes), now);
            intents.Add(intent);
            escrow.StartPayment(intent.Reference, actor, now);
            await commit.SaveAsync(ct, escrow);
        }
        else if (escrow.State == EscrowState.PaymentPending)
        {
            intent = (await intents.ListForEscrowAsync(escrowId, ct)).FirstOrDefault(i => i.IsOpen)
                     ?? throw new InvalidEscrowTransitionException(escrow.State, "start payment");
        }
        else throw new InvalidEscrowTransitionException(escrow.State, "start payment");

        if (intent.Status == PaymentIntentStatus.Pending)
            return new CollectionStart(intent.Reference, intent.ProviderRef, intent.ExpiresAt, escrow.State.ToString());

        var buyer = await parties.GetAsync(escrow.BuyerId, ct) ?? throw new NotFoundException("Buyer not found.");
        var requested = await provider.RequestCollectionAsync(
            new CollectionRequest(intent.Reference, intent.Amount, intent.Currency, buyer.Phone, buyer.WalletNetwork, $"Order {escrow.OrderNumber}"), ct);

        intent.MarkPending(requested.AccountNumber ?? requested.ProviderRef, requested.ExpiresAt);
        notifier.Queue(escrow, buyer.Phone, "payment_instructions", SmsTemplates.PaymentInstructions(escrow, requested.AccountNumber, intent.ExpiresAt, now));
        await commit.SaveAsync(ct);
        return new CollectionStart(intent.Reference, intent.ProviderRef, intent.ExpiresAt, escrow.State.ToString());
    }

    /// <summary>Applies a collection notification. Safe to call any number of times with the same event.</summary>
    public async Task<CallbackOutcome> HandleAsync(CollectionNotification n, WebhookInboxItem? inbox, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var escrow = await escrows.GetByPaymentReferenceAsync(n.Reference, ct);
        var intent = await intents.GetByReferenceAsync(n.Reference, ct);
        if (escrow is null || intent is null)
        {
            inbox?.MarkProcessed(CallbackOutcome.UnknownReference.ToString(), now);
            await commit.SaveAsync(ct);
            return CallbackOutcome.UnknownReference;
        }

        var outcome = CallbackOutcome.Ignored;
        var buyer = await parties.GetAsync(escrow.BuyerId, ct);
        var seller = await parties.GetAsync(escrow.SellerId, ct);

        switch (n.Kind)
        {
            case CollectionEventKind.Succeeded when escrow.State == EscrowState.PaymentPending:
                if (n.Amount != escrow.Total || !string.Equals(n.Currency, escrow.Currency, StringComparison.OrdinalIgnoreCase))
                {
                    intent.FlagMismatch();
                    escrow.FlagForOps($"Wrong amount received: {Money.Format(n.Amount, n.Currency)} instead of {Money.Format(escrow.Total, escrow.Currency)}", Actors.System, now);
                    notifier.Queue(escrow, buyer!.Phone, "wrong_amount", SmsTemplates.WrongAmount(escrow, n.Amount));
                    outcome = CallbackOutcome.AmountMismatch;
                    break;
                }

                intent.Succeed();
                escrow.MarkFunded(Actors.System, now, n.EventKey);
                ledger.Add(EscrowPostings.Funding(escrow, $"funding:{n.EventKey}", now));
                if (n.Fee > 0) ledger.Add(EscrowPostings.CollectionFee(escrow, n.Fee, $"collection-fee:{n.EventKey}", now));

                var courier = await couriers.GetAsync(escrow.CourierId, ct);
                notifier.Queue(escrow, seller!.Phone, "funded_seller", SmsTemplates.FundedSeller(escrow, courier?.Name ?? "your courier"));
                if (courier is not null) notifier.Queue(escrow, courier.Phone, "funded_courier", SmsTemplates.FundedCourier(escrow, seller.Name));
                outcome = CallbackOutcome.Processed;
                break;

            case CollectionEventKind.Succeeded:
                // Money arrived for an order that is cancelled or already funded. Never silently swallow it.
                if (escrow.State is EscrowState.Cancelled)
                    escrow.FlagForOps($"Payment of {Money.Format(n.Amount, n.Currency)} received after the order was cancelled: refund the buyer manually.", Actors.System, now);
                else if (escrow.State != EscrowState.PaymentPending && intent.Status == PaymentIntentStatus.Succeeded)
                    escrow.FlagForOps($"A second payment of {Money.Format(n.Amount, n.Currency)} was received (event {n.EventKey}): refund the buyer manually.", Actors.System, now);
                break;

            case CollectionEventKind.Failed when escrow.State == EscrowState.PaymentPending:
                intent.Fail();
                escrow.Cancel("Payment failed", Actors.System, now);
                notifier.Queue(escrow, buyer!.Phone, "payment_failed", SmsTemplates.PaymentFailed(escrow));
                notifier.Queue(escrow, seller!.Phone, "payment_failed", SmsTemplates.PaymentFailed(escrow));
                outcome = CallbackOutcome.Processed;
                break;

            case CollectionEventKind.Reversed:
                escrow.FlagForOps($"The provider reversed the collection ({n.EventKey}). Check the money before releasing anything.", Actors.System, now);
                break;
        }

        inbox?.MarkProcessed(outcome.ToString(), now);
        try
        {
            await commit.SaveAsync(ct, escrow);
        }
        catch (DuplicateOperationException)
        {
            return CallbackOutcome.Duplicate; // a concurrent delivery of the same event won the race
        }
        return outcome;
    }

    /// <summary>Cancels orders whose payment window passed without payment.</summary>
    public async Task<int> ExpireDueAsync(CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var count = 0;
        foreach (var intent in (await intents.ListOpenAsync(100, ct)).Where(i => i.ExpiresAt <= now))
        {
            var escrow = await escrows.GetAsync(intent.EscrowId, ct);
            intent.Expire();
            if (escrow is { State: EscrowState.PaymentPending })
            {
                escrow.Cancel("Payment window expired", Actors.System, now);
                foreach (var p in await parties.GetManyAsync([escrow.BuyerId, escrow.SellerId], ct))
                    notifier.Queue(escrow, p.Phone, "payment_expired", SmsTemplates.PaymentExpired(escrow));
            }
            await commit.SaveAsync(ct, escrow is null ? [] : [escrow]);
            count++;
        }
        return count;
    }

    /// <summary>Asks the provider about intents that have been waiting a while, so a lost webhook cannot strand an order.</summary>
    public async Task<int> PollPendingAsync(CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var count = 0;
        foreach (var intent in (await intents.ListOpenAsync(100, ct))
                     .Where(i => i.Status == PaymentIntentStatus.Pending && i.ExpiresAt > now && now - (i.LastPolledAt ?? i.CreatedAt) >= PollEvery))
        {
            var status = await provider.GetCollectionStatusAsync(intent.Reference, ct);
            intent.Polled(now);
            if (status.Status is RemoteStatus.Succeeded or RemoteStatus.Failed)
            {
                var kind = status.Status == RemoteStatus.Succeeded ? CollectionEventKind.Succeeded : CollectionEventKind.Failed;
                await HandleAsync(new CollectionNotification($"poll:{intent.Reference}:{kind}", intent.Reference, kind,
                    status.Amount ?? intent.Amount, status.Fee, intent.Currency, intent.ProviderRef), null, ct);
            }
            else await commit.SaveAsync(ct);
            count++;
        }
        return count;
    }
}
