using System.Text.Json;
using CourierEscrow.Domain;

namespace CourierEscrow.Application.Services;

/// <summary>Sends queued SMS with retries. A sensitive body (the OTP) is decrypted only for the send and blanked afterwards.</summary>
public sealed class NotificationDispatcher(INotificationStore store, ISmsSender sender, IProtector protector, IUnitOfWork unitOfWork, TimeProvider clock, ISmsCapture capture)
{
    public async Task<int> DispatchDueAsync(CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var sent = 0;
        foreach (var n in await store.ListDueAsync(now, 25, ct))
        {
            try
            {
                var body = n.Sensitive ? protector.Unprotect(n.Body) : n.Body;
                n.MarkSent(await sender.SendAsync(n.ToPhone, body, ct), now);
                capture.Record(n.EscrowId, n.ToPhone, n.Template, body, now);
                sent++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                n.MarkFailed(ex.Message, now);
            }
            await unitOfWork.CommitAsync(ct);
        }
        return sent;
    }
}

public enum WebhookReceipt { Unauthorized, Ignored, Duplicate, Stored }

/// <summary>
/// Provider callbacks: verify the signature, store the raw payload, answer quickly. Processing happens next,
/// and again from a worker if it fails, so a callback can never be lost.
/// </summary>
public sealed class WebhookService(
    IPaymentProvider provider, IWebhookInbox inbox, CollectionService collections, PayoutService payouts,
    IUnitOfWork unitOfWork, TimeProvider clock)
{
    public async Task<(WebhookReceipt Receipt, Guid? ItemId)> ReceiveAsync(byte[] raw, string? signature, string? eventCode, CancellationToken ct = default)
    {
        if (!provider.VerifyWebhook(raw, signature)) return (WebhookReceipt.Unauthorized, null);

        var body = System.Text.Encoding.UTF8.GetString(raw);
        var notification = provider.ParseWebhook(eventCode, body);
        if (notification is null) return (WebhookReceipt.Ignored, null);
        if (await inbox.ExistsAsync(notification.EventKey, ct)) return (WebhookReceipt.Duplicate, null);

        var item = WebhookInboxItem.Create(provider.Name, notification.EventKey, eventCode ?? "", body, clock.GetUtcNow());
        inbox.Add(item);
        try { await unitOfWork.CommitAsync(ct); }
        catch (DuplicateOperationException) { return (WebhookReceipt.Duplicate, null); }
        return (WebhookReceipt.Stored, item.Id);
    }

    /// <summary>Applies one stored callback. Throws on failure; the caller records it with <see cref="RecordFailureAsync"/> in a fresh scope.</summary>
    public async Task<string> ProcessAsync(Guid itemId, CancellationToken ct = default)
    {
        var item = await inbox.GetAsync(itemId, ct) ?? throw new NotFoundException("Inbox item not found.");
        if (item.Status == WebhookStatus.Processed) return item.Outcome ?? "Processed";

        var notification = provider.ParseWebhook(item.EventCode, item.RawPayload);
        if (notification is null)
        {
            item.MarkProcessed("Ignored", clock.GetUtcNow());
            await unitOfWork.CommitAsync(ct);
            return "Ignored";
        }

        var outcome = notification switch
        {
            CollectionNotification c => await collections.HandleAsync(c, item, ct),
            PayoutNotification p => await payouts.ApplyAsync(p, item, ct),
            _ => CallbackOutcome.Ignored
        };
        return outcome.ToString();
    }

    public async Task RecordFailureAsync(Guid itemId, string error, CancellationToken ct = default)
    {
        var item = await inbox.GetAsync(itemId, ct);
        if (item is null) return;
        item.MarkFailed(error);
        await unitOfWork.CommitAsync(ct);
    }

    public Task<IReadOnlyList<WebhookInboxItem>> ListPendingAsync(int take, CancellationToken ct = default) => inbox.ListPendingAsync(take, ct);
}

public sealed record ReconciliationResult(ReconciliationRun Run, IReadOnlyList<ReconciliationItem> Items);

/// <summary>Daily checks: the provider's payouts against ours, our clearing balance against theirs, and every escrow against its state.</summary>
public sealed class ReconciliationService(
    IPaymentProvider provider, IPayoutRepository payouts, IEscrowRepository escrows, ILedgerStore ledger,
    IReconciliationStore store, IUnitOfWork unitOfWork, TimeProvider clock, EscrowOptions options)
{
    private static readonly EscrowState[] HoldingStates =
        [EscrowState.Funded, EscrowState.PickedUp, EscrowState.Delivered, EscrowState.Rejected, EscrowState.Returned, EscrowState.Disputed];

    public async Task<ReconciliationResult> RunAsync(DateOnly day, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var items = new List<(ReconciliationKind Kind, string Ref, string Detail)>();

        var remote = await provider.ListPayoutsAsync(day, ct);
        var ours = await payouts.ListSubmittedOnAsync(day, ct);
        var byRef = ours.Where(p => p.ProviderRef is not null).ToDictionary(p => p.ProviderRef!, StringComparer.OrdinalIgnoreCase);
        var bySource = ours.ToDictionary(p => p.Reference, StringComparer.OrdinalIgnoreCase);
        var matchedOurs = new HashSet<Guid>();
        var matched = 0;

        foreach (var r in remote)
        {
            Payout? p = byRef.GetValueOrDefault(r.TransactionRef) ?? (r.SourceReference is null ? null : bySource.GetValueOrDefault(r.SourceReference));
            if (p is null) { items.Add((ReconciliationKind.ProviderOnly, r.TransactionRef, $"Provider shows {Money.Format(r.Amount)} ({r.Status}) with no payout on our side.")); continue; }

            matchedOurs.Add(p.Id);
            if (p.Amount != r.Amount)
                items.Add((ReconciliationKind.Mismatch, p.Reference, $"Amount differs: we sent {Money.Format(p.Amount)}, provider has {Money.Format(r.Amount)}."));
            else if (r.Status == RemoteStatus.Succeeded && p.Status != PayoutStatus.Confirmed)
                items.Add((ReconciliationKind.Mismatch, p.Reference, "Provider says paid but we have not confirmed it."));
            else if (r.Status == RemoteStatus.Failed && p.Status == PayoutStatus.Confirmed)
                items.Add((ReconciliationKind.Mismatch, p.Reference, "We marked this paid but the provider says it failed."));
            else matched++;
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
    public Task<IReadOnlyList<ReconciliationItem>> ListItemsAsync(Guid runId, CancellationToken ct = default) => store.ListItemsAsync(runId, ct);
}

public sealed record AlertSummary(
    int PaymentsStuck, int PayoutsNeedingReview, int PayoutsStuckSubmitted, int WebhooksUnprocessed,
    int DisputesOverdue, int OrdersFlagged, int LastReconciliationExceptions, bool LedgerBalanced)
{
    public bool AllClear => PaymentsStuck + PayoutsNeedingReview + PayoutsStuckSubmitted + WebhooksUnprocessed + DisputesOverdue
                            + OrdersFlagged + LastReconciliationExceptions == 0 && LedgerBalanced;
}

/// <summary>What needs a human right now. Powers the admin alert strip.</summary>
public sealed class AlertsService(
    IPaymentIntentRepository intents, IPayoutRepository payouts, IWebhookInbox inbox, IDisputeRepository disputes,
    IEscrowRepository escrows, IReconciliationStore recon, ILedgerStore ledger, TimeProvider clock, EscrowOptions options)
{
    public async Task<AlertSummary> GetAsync(CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var stuck = (await intents.ListOpenAsync(200, ct)).Count(i => i.ExpiresAt <= now.AddMinutes(-5));
        var review = (await payouts.ListAsync(PayoutStatus.NeedsReview, 200, ct)).Count;
        var submitted = (await payouts.ListSubmittedBeforeAsync(now.AddMinutes(-30), 200, ct)).Count;
        var inboxPending = (await inbox.ListPendingAsync(200, ct)).Count(i => now - i.ReceivedAt > TimeSpan.FromMinutes(2));
        var overdue = (await disputes.ListAsync(DisputeStatus.Open, 200, ct)).Count(d => d.DueAt <= now);
        var flagged = (await escrows.ListFlaggedAsync(200, ct)).Count;
        var lastRun = (await recon.ListRunsAsync(1, ct)).FirstOrDefault();
        var summary = await ledger.GetSummaryAsync(options.DefaultCurrency, ct);
        return new(stuck, review, submitted, inboxPending, overdue, flagged, lastRun?.ExceptionCount ?? 0, summary.IsBalanced);
    }
}
