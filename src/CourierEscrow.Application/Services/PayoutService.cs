using CourierEscrow.Domain;
using CourierEscrow.Domain.Ledger;

namespace CourierEscrow.Application.Services;

/// <summary>
/// Money leaving the platform. A settlement posting creates payout rows in the same transaction; a worker submits them
/// with stable keys; only a CONFIRMED payout posts the entry that clears the payable; an order is SETTLED only when
/// every payout is confirmed.
/// </summary>
public sealed class PayoutService(
    IEscrowRepository escrows, IPartyRepository parties, ICourierRepository couriers, IPayoutRepository payouts,
    ILedgerStore ledger, IPaymentProvider provider, IAuditLog audit, EscrowCommit commit, TimeProvider clock)
{
    private static readonly TimeSpan SubmittedPollAfter = TimeSpan.FromMinutes(2);

    /// <summary>Stages the settlement ledger posting and one payout per payee. The caller commits.</summary>
    public async Task StageSettlementAsync(Escrow escrow, Settlement s, string kind, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        ledger.Add(EscrowPostings.Settle(escrow, s, kind, now));

        if (s.Seller > 0)
        {
            var seller = await parties.GetAsync(escrow.SellerId, ct) ?? throw new NotFoundException("Seller not found.");
            payouts.Add(Payout.Create(escrow, PayoutKind.Seller, seller.Id, seller.Name, seller.Phone, seller.WalletNetwork, s.Seller, now));
        }
        if (s.Courier > 0)
        {
            var courier = await couriers.GetAsync(escrow.CourierId, ct) ?? throw new NotFoundException("Courier not found.");
            payouts.Add(Payout.Create(escrow, PayoutKind.Courier, courier.Id, courier.Name, courier.Phone, courier.WalletNetwork, s.Courier, now));
        }
        if (s.BuyerRefund > 0)
        {
            var buyer = await parties.GetAsync(escrow.BuyerId, ct) ?? throw new NotFoundException("Buyer not found.");
            payouts.Add(Payout.Create(escrow, PayoutKind.BuyerRefund, buyer.Id, buyer.Name, buyer.Phone, buyer.WalletNetwork, s.BuyerRefund, now));
        }
    }

    /// <summary>Submits payouts that are due. Each is committed on its own so one failure cannot hold up the rest.</summary>
    public async Task<int> ExecuteDueAsync(CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var count = 0;
        foreach (var p in await payouts.ListDueAsync(now, 20, ct))
        {
            try
            {
                var result = await provider.SubmitPayoutAsync(new PayoutInstruction(
                    p.Reference, p.Amount, p.Currency, p.BeneficiaryName, p.Msisdn, p.Network, $"Nexa Escrow {p.Kind}"), ct);

                switch (result.Outcome)
                {
                    case PayoutSubmissionOutcome.Accepted: p.MarkSubmitted(result.ProviderRef, now); break;
                    case PayoutSubmissionOutcome.Transient: p.RecordTransientFailure(result.Message ?? "Temporary provider error", now); break;
                    default: p.NeedsAttention(result.Message ?? "Rejected by the provider"); break;
                }
            }
            catch (ProviderException ex)
            {
                if (ex.Transient) p.RecordTransientFailure(ex.Message, now); else p.NeedsAttention(ex.Message);
            }
            await commit.SaveAsync(ct);
            count++;
        }
        return count;
    }

    /// <summary>Asks the provider about submitted payouts whose webhook has not arrived.</summary>
    public async Task<int> PollSubmittedAsync(CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var count = 0;
        foreach (var p in await payouts.ListSubmittedBeforeAsync(now - SubmittedPollAfter, 20, ct))
        {
            var status = await provider.GetPayoutStatusAsync(p.Reference, p.ProviderRef, DateOnly.FromDateTime((p.SubmittedAt ?? now).UtcDateTime), ct);
            if (status.Status == RemoteStatus.Succeeded)
                await ApplyAsync(new PayoutNotification($"poll:{p.Reference}:ok", p.Reference, PayoutEventKind.Succeeded, status.Amount ?? p.Amount, status.Fee, null, p.ProviderRef), null, ct);
            else if (status.Status == RemoteStatus.Failed)
                await ApplyAsync(new PayoutNotification($"poll:{p.Reference}:failed", p.Reference, PayoutEventKind.Failed, p.Amount, 0, status.Reason, p.ProviderRef), null, ct);
            count++;
        }
        return count;
    }

    public async Task<CallbackOutcome> ApplyAsync(PayoutNotification n, WebhookInboxItem? inbox, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var payout = await payouts.GetByReferenceAsync(n.SourceReference, ct);
        if (payout is null)
        {
            inbox?.MarkProcessed(CallbackOutcome.UnknownReference.ToString(), now);
            await commit.SaveAsync(ct);
            return CallbackOutcome.UnknownReference;
        }

        var escrow = await escrows.GetAsync(payout.EscrowId, ct) ?? throw new NotFoundException("Order not found.");
        var outcome = CallbackOutcome.Ignored;

        switch (n.Kind)
        {
            case PayoutEventKind.Succeeded when payout.Status != PayoutStatus.Confirmed:
                if (n.Amount != payout.Amount)
                {
                    payout.NeedsAttention($"Provider reports {Money.Format(n.Amount, payout.Currency)} but we sent {Money.Format(payout.Amount, payout.Currency)}.");
                    escrow.FlagForOps($"Payout {payout.Reference} amount mismatch.", Actors.System, now);
                    outcome = CallbackOutcome.AmountMismatch;
                    break;
                }
                payout.Confirm(n.Fee, n.ProviderRef, now);
                ledger.Add(EscrowPostings.PayoutConfirmed(payout, n.Fee, now));
                if (escrow.State is EscrowState.Released or EscrowState.Refunded
                    && (await payouts.ListForEscrowAsync(escrow.Id, ct)).All(x => x.Status == PayoutStatus.Confirmed))
                    escrow.Settle(Actors.System, now);
                outcome = CallbackOutcome.Processed;
                break;

            case PayoutEventKind.Failed when payout.Status is PayoutStatus.Submitted or PayoutStatus.Queued:
                payout.FailedAtProvider(n.Reason ?? "Payout failed at the provider", now);
                outcome = CallbackOutcome.Processed;
                break;

            case PayoutEventKind.Reversed when payout.Status == PayoutStatus.Confirmed:
                ledger.Add(EscrowPostings.PayoutReversed(payout, n.EventKey, now));
                payout.Reversed("Reversed by the provider");
                escrow.FlagForOps($"Payout {payout.Reference} was reversed by the provider.", Actors.System, now);
                outcome = CallbackOutcome.Processed;
                break;
        }

        inbox?.MarkProcessed(outcome.ToString(), now);
        try { await commit.SaveAsync(ct, escrow); }
        catch (DuplicateOperationException) { return CallbackOutcome.Duplicate; }
        return outcome;
    }

    /// <summary>Ops retry after fixing the cause. A new key is used so the provider does not replay the earlier refusal.</summary>
    public async Task<Payout> RetryAsync(Guid payoutId, bool newKey, string? correctedMsisdn, string? correctedNetwork, string actor, CancellationToken ct = default)
    {
        var p = await payouts.GetAsync(payoutId, ct) ?? throw new NotFoundException("Payout not found.");
        if (correctedMsisdn is not null)
        {
            p.CorrectWallet(correctedMsisdn, correctedNetwork ?? p.Network ?? throw new DomainValidationException("A wallet network is required."));
            newKey = true;
        }
        else if (correctedNetwork is not null) { p.CorrectWallet(p.Msisdn, correctedNetwork); newKey = true; }

        p.Retry(newKey, clock.GetUtcNow());
        audit.Add(AuditEntry.Create(actor, "payout.retry", "payout", payoutId.ToString(), newKey ? "new key" : "same key", clock.GetUtcNow()));
        await commit.SaveAsync(ct);
        return p;
    }

    public Task<IReadOnlyList<Payout>> ListAsync(PayoutStatus? status, int take = 100, CancellationToken ct = default) =>
        payouts.ListAsync(status, Math.Clamp(take, 1, 200), ct);
}
