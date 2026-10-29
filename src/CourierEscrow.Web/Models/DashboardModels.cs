namespace CourierEscrow.Web.Models;

public sealed record LedgerSummary(string Currency, long ProviderBalance, long HeldInEscrow, long OwedToSellers, long OwedToCouriers, long OwedToBuyers, long PlatformRevenue, long ProviderFees, bool IsBalanced);
public sealed record Alerts(int PaymentsStuck, int PayoutsNeedingReview, int PayoutsStuckSubmitted, int WebhooksUnprocessed, int DisputesOverdue, int OrdersFlagged, int LastReconciliationExceptions, bool LedgerBalanced, bool AllClear);
public sealed record Summary(LedgerSummary Ledger, Alerts Alerts);
public sealed record ReconRun(Guid Id, string ForDate, DateTimeOffset RanAt, int ProviderCount, int MatchedCount, int ExceptionCount);
public sealed record ReconItem(string Kind, string Reference, string Detail);
