namespace CourierEscrow.Web.Services;

// Mirrors the API's JSON. Enums arrive as strings.
public sealed record OrderSummary(Guid Id, string OrderNumber, string State, string ItemDescription, long ItemAmount, long DeliveryFee, long PlatformFee, long Total,
    string Currency, string BuyerName, string BuyerPhone, string SellerName, string SellerPhone, string? OpsFlag, DateTimeOffset CreatedAt,
    string? TrackingToken = null, DateTimeOffset? ProductConfirmedAt = null);
public sealed record EventRow(string Type, string From, string To, string Actor, string? ClientEventId, string? Payload, DateTimeOffset At);
public sealed record ProofRow(Guid Id, string Kind, string Sha256, double? Latitude, double? Longitude, DateTimeOffset ServerTime, DateTimeOffset? ClientTime);
public sealed record LedgerEntryRow(string Account, string Direction, long Amount);
public sealed record LedgerTx(Guid Id, Guid EscrowId, string Kind, DateTimeOffset At, List<LedgerEntryRow> Entries);
public sealed record PayoutRow(Guid Id, Guid EscrowId, string Kind, string Beneficiary, string Wallet, string? Network, long Amount, string Currency, string Reference, string Status, int Attempts, string? LastError, long Fee, DateTimeOffset CreatedAt);
public sealed record IntentRow(string Reference, string Status, long Amount, DateTimeOffset ExpiresAt);
public sealed record DisputeRow(Guid Id, Guid EscrowId, string RaisedBy, string Reason, string Status, string PriorState, DateTimeOffset OpenedAt, DateTimeOffset DueAt, string? Outcome, long BuyerAmount, long SellerAmount, long CourierAmount, long PlatformAmount, string? DecidedBy, string? DecisionReason);
public sealed record OrderView(OrderSummary Order, string? CourierName, string? SealId, int? WeightGrams, string? OtpStatus, int OtpAttempts,
    List<EventRow> Events, List<ProofRow> Proofs, List<LedgerTx> Ledger, List<PayoutRow> Payouts, List<IntentRow> Payments, List<DisputeRow> Disputes, long HeldInEscrow);
public sealed record DevSmsRow(DateTimeOffset At, string ToPhone, string Template, string Body);
public sealed record StaffUserRow(Guid Id, string Username, string DisplayName, string Role, string Status, bool MustChangePassword, bool Locked, DateTimeOffset CreatedAt, DateTimeOffset? LastLoginAt);
public sealed record LoginResponse(string Token, DateTimeOffset ExpiresAt, StaffUserRow User);
public sealed record CourierRow(Guid Id, string Name, string Phone, string Network, string Status, bool DeviceBound);
public sealed record LedgerSummary(string Currency, long ProviderBalance, long HeldInEscrow, long OwedToSellers, long OwedToCouriers, long OwedToBuyers, long PlatformRevenue, long ProviderFees, bool IsBalanced);
public sealed record Alerts(int PaymentsStuck, int PayoutsNeedingReview, int PayoutsStuckSubmitted, int WebhooksUnprocessed, int DisputesOverdue, int OrdersFlagged, int LastReconciliationExceptions, bool LedgerBalanced, bool AllClear);
public sealed record Summary(LedgerSummary Ledger, Alerts Alerts);
public sealed record PaymentInfo(string Reference, string? AccountNumber, DateTimeOffset ExpiresAt, string State);
public sealed record ReconRun(Guid Id, string ForDate, DateTimeOffset RanAt, int ProviderCount, int MatchedCount, int ExceptionCount);
public sealed record ReconItem(string Kind, string Reference, string Detail);

public sealed record NewOrderForm
{
    public string BuyerName { get; set; } = "";
    public string BuyerPhone { get; set; } = "";
    public string? BuyerNetwork { get; set; }
    public string SellerName { get; set; } = "";
    public string SellerPhone { get; set; } = "";
    public string? SellerNetwork { get; set; }
    public Guid CourierId { get; set; }
    public string ItemDescription { get; set; } = "";
    public string ItemKwacha { get; set; } = "";
    public string DeliveryKwacha { get; set; } = "";
    public string? PickupAddress { get; set; }
    public string? DeliveryAddress { get; set; }
}
