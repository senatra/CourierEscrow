using CourierEscrow.Application.Helpers;
using CourierEscrow.Application.Interfaces;
using CourierEscrow.Domain.Models;

namespace CourierEscrow.Application.Services;

public sealed record ReconciliationResult(ReconciliationRun Run, IReadOnlyList<ReconciliationItem> Items);

public sealed class ReconciliationService(
    IPaymentProvider provider, IPayoutRepository payouts, IEscrowRepository escrows, ILedgerStore ledger,
    IReconciliationStore store, IUnitOfWork unitOfWork, TimeProvider clock, EscrowOptions options, PayoutService payoutService)
{
    private static readonly EscrowState[] HoldingStates =
        [EscrowState.Funded, EscrowState.PickedUp, EscrowState.Delivered, EscrowState.Rejected, EscrowState.Returned, EscrowState.Disputed];

    public async Task<ReconciliationResult> RunAsync(DateOnly day, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var items = new List<(ReconciliationKind Kind, string Ref, string Detail)>();

        var supportsPayoutInquiry = provider.SupportsPayoutInquiry;
        IReadOnlyList<ProviderPayoutRecord> remote = supportsPayoutInquiry ? await provider.ListPayoutsAsync(day, ct) : [];
        IReadOnlyList<Payout> ours = supportsPayoutInquiry ? await payouts.ListSubmittedOnAsync(day, ct) : [];
        if (!supportsPayoutInquiry)
            items.Add((ReconciliationKind.Mismatch, "provider", $"{provider.Name} does not provide payout inquiry; payout status comparison was skipped."));
        var byRef = ours.Where(p => !string.IsNullOrWhiteSpace(p.ProviderRef))
            .GroupBy(p => p.ProviderRef!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var bySource = ours.ToDictionary(p => p.Reference, StringComparer.OrdinalIgnoreCase);
        var matchedOurs = new HashSet<Guid>();
        var matched = 0;

        foreach (var r in remote)
        {
            Payout? p = byRef.GetValueOrDefault(r.TransactionRef) ?? (r.SourceReference is null ? null : bySource.GetValueOrDefault(r.SourceReference));
            if (p is null) { items.Add((ReconciliationKind.ProviderOnly, r.TransactionRef, $"Provider shows {Money.Format(r.Amount)} ({r.Status}) with no payout on our side.")); continue; }

            matchedOurs.Add(p.Id);
            if (!string.IsNullOrWhiteSpace(r.Currency) && !string.Equals(p.Currency, r.Currency, StringComparison.OrdinalIgnoreCase))
            {
                var reason = $"Currency differs: we sent {p.Currency}, provider reports {r.Currency}.";
                await payoutService.FlagProviderStatusAsync(p.Reference, reason, ct);
                items.Add((ReconciliationKind.Mismatch, p.Reference, $"Currency differs: we sent {p.Currency}, provider reports {r.Currency}."));
            }
            else if (p.Amount != r.Amount && r.Status == RemoteStatus.Succeeded)
            {
                var outcome = await payoutService.ApplyAsync(new PayoutNotification(
                    $"reconciliation:{day:yyyy-MM-dd}:{r.TransactionRef}:succeeded", p.Reference,
                    PayoutEventKind.Succeeded, r.Amount, r.Fee, null, r.TransactionRef), null, ct);
                if (outcome != CallbackOutcome.AmountMismatch)
                    await payoutService.FlagProviderStatusAsync(p.Reference, "Provider reported a successful payout with an amount that differs from our instruction.", ct);
                items.Add((ReconciliationKind.Mismatch, p.Reference, $"Amount differs: we sent {Money.Format(p.Amount)}, provider has {Money.Format(r.Amount)}. The payout was marked for review."));
            }
            else if (p.Amount != r.Amount)
            {
                var reason = $"Amount differs: we sent {Money.Format(p.Amount)}, provider has {Money.Format(r.Amount)}.";
                await payoutService.FlagProviderStatusAsync(p.Reference, reason, ct);
                items.Add((ReconciliationKind.Mismatch, p.Reference, reason));
            }
            else if (r.Status == RemoteStatus.Succeeded)
            {
                var outcome = await payoutService.ApplyAsync(new PayoutNotification(
                    $"reconciliation:{day:yyyy-MM-dd}:{r.TransactionRef}:succeeded", p.Reference,
                    PayoutEventKind.Succeeded, r.Amount, r.Fee, null, r.TransactionRef), null, ct);
                if (outcome == CallbackOutcome.AmountMismatch)
                    items.Add((ReconciliationKind.Mismatch, p.Reference, "Provider reported a successful payout with an amount that needs review."));
                else matched++;
            }
            else if (r.Status == RemoteStatus.Failed)
            {
                await payoutService.FlagProviderStatusAsync(p.Reference, "Provider reports the payout failed; review before retrying.", ct);
                items.Add((ReconciliationKind.Mismatch, p.Reference, p.Status == PayoutStatus.Confirmed
                    ? "We marked this paid but the provider reports it failed."
                    : "Provider reports this payout failed; it was marked for review before retry."));
            }
            else if (r.Status == RemoteStatus.Reversed)
            {
                if (p.Status == PayoutStatus.Confirmed)
                    await payoutService.ApplyAsync(new PayoutNotification(
                        $"reconciliation:{day:yyyy-MM-dd}:{r.TransactionRef}:reversed", p.Reference,
                        PayoutEventKind.Reversed, r.Amount, r.Fee, "Provider reports the payout was reversed.", r.TransactionRef), null, ct);
                else
                    await payoutService.FlagProviderStatusAsync(p.Reference, "Provider reports a reversal before we confirmed the payout.", ct);
                items.Add((ReconciliationKind.Mismatch, p.Reference, p.Status == PayoutStatus.Confirmed
                    ? "Provider reversed this payout; it is marked for operations review."
                    : "Provider reports a reversal, but this payout was not confirmed internally."));
            }
            else if (r.Status == RemoteStatus.RequiresReview)
            {
                var reason = $"OneKhusa reports payout status {r.StatusCode ?? r.Status.ToString()}; manual review is required.";
                await payoutService.FlagProviderStatusAsync(p.Reference, reason, ct);
                items.Add((ReconciliationKind.Mismatch, p.Reference, reason));
            }
            else if (r.Status == RemoteStatus.Pending && p.Status is PayoutStatus.Submitted or PayoutStatus.Queued)
                matched++;
            else if (r.Status == RemoteStatus.Pending)
            {
                var reason = p.Status == PayoutStatus.Confirmed
                    ? "Provider reports this payout as pending after we marked it confirmed."
                    : "Provider reports this payout as pending while our payout is marked for review.";
                await payoutService.FlagProviderStatusAsync(p.Reference, reason, ct);
                items.Add((ReconciliationKind.Mismatch, p.Reference, reason));
            }
            else
            {
                await payoutService.FlagProviderStatusAsync(p.Reference, "Provider returned an unrecognized payout status.", ct);
                items.Add((ReconciliationKind.Mismatch, p.Reference, "Provider returned an unrecognized payout status; the payout was marked for manual review."));
            }
        }

        foreach (var p in ours.Where(p => !matchedOurs.Contains(p.Id)))
            items.Add((ReconciliationKind.InternalOnly, p.Reference, $"We submitted {Money.Format(p.Amount)} but the provider has no record."));

        var currency = options.DefaultCurrency;
        var summary = await ledger.GetSummaryAsync(currency, ct);
        if (!summary.IsBalanced) items.Add((ReconciliationKind.ClearingBalance, "ledger", "The ledger does not balance: clearing is not equal to escrow plus payables plus revenue less fees."));

        var providerBalance = await provider.GetBalanceAsync(currency, ct);
        if (providerBalance is not null && providerBalance != summary.ProviderBalance)
            items.Add((ReconciliationKind.ClearingBalance, "clearing", $"Ledger clearing {Money.Format(summary.ProviderBalance)} but the provider reports {Money.Format(providerBalance.Value)}."));

        var held = await ledger.GetEscrowBalancesAsync(currency, ct);
        foreach (var e in await escrows.ListByStatesAsync(HoldingStates, 500, ct))
            if (held.GetValueOrDefault(e.Id) != e.Total)
                items.Add((ReconciliationKind.EscrowBalance, e.OrderNumber, $"{e.State} order should hold {Money.Format(e.Total)} but holds {Money.Format(held.GetValueOrDefault(e.Id))}."));
        foreach (var (id, balance) in held.Where(kv => kv.Value < 0))
            items.Add((ReconciliationKind.EscrowBalance, id.ToString(), $"Escrow balance is negative ({Money.Format(balance)})."));

        var run = ReconciliationRun.Create(day, remote.Count, matched, items.Count, now);
        store.Add(run);
        var rows = items.Select(i => ReconciliationItem.Create(run.Id, i.Kind, i.Ref, i.Detail)).ToList();
        foreach (var row in rows) store.Add(row);
        await unitOfWork.CommitAsync(ct);
        return new(run, rows);
    }

    public Task<IReadOnlyList<ReconciliationRun>> ListRunsAsync(int take = 30, CancellationToken ct = default) => store.ListRunsAsync(take, ct);
    public Task<bool> HasRunForDateAsync(DateOnly day, CancellationToken ct = default) => store.HasRunForDateAsync(day, ct);
    public Task<IReadOnlyList<ReconciliationItem>> ListItemsAsync(Guid runId, CancellationToken ct = default) => store.ListItemsAsync(runId, ct);
}
