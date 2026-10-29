using CourierEscrow.Application.Helpers;
using CourierEscrow.Application.Interfaces;
using CourierEscrow.Domain.Models;

namespace CourierEscrow.Application.Services;

public sealed record AlertSummary(
    int PaymentsStuck, int PayoutsNeedingReview, int PayoutsStuckSubmitted, int WebhooksUnprocessed,
    int DisputesOverdue, int OrdersFlagged, int LastReconciliationExceptions, bool LedgerBalanced)
{
    public bool AllClear => PaymentsStuck + PayoutsNeedingReview + PayoutsStuckSubmitted + WebhooksUnprocessed + DisputesOverdue
                            + OrdersFlagged + LastReconciliationExceptions == 0 && LedgerBalanced;
}

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
