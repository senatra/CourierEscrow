namespace CourierEscrow.Domain;

public enum DisputeStatus { Open = 0, Decided = 1 }
public enum DisputeOutcome { ReleaseToSeller = 0, RefundBuyerKeepDeliveryFee = 1, RefundBuyerFull = 2, Split = 3 }

public sealed class Dispute
{
    public static readonly TimeSpan DecisionWindow = TimeSpan.FromHours(48);

    private Dispute() { RaisedBy = ""; Reason = ""; }

    public Guid Id { get; private set; }
    public Guid EscrowId { get; private set; }
    public string RaisedBy { get; private set; }
    public string Reason { get; private set; }
    public DisputeStatus Status { get; private set; }
    public EscrowState PriorState { get; private set; }
    public DateTimeOffset OpenedAt { get; private set; }
    public DateTimeOffset DueAt { get; private set; }
    public DisputeOutcome? Outcome { get; private set; }
    public long BuyerAmount { get; private set; }
    public long SellerAmount { get; private set; }
    public long CourierAmount { get; private set; }
    public long PlatformAmount { get; private set; }
    public string? DecidedBy { get; private set; }
    public string? DecisionReason { get; private set; }
    public DateTimeOffset? DecidedAt { get; private set; }

    public static Dispute Open(Escrow e, string raisedBy, string reason, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new DomainValidationException("A reason is required to open a dispute.");
        return new Dispute { Id = Guid.NewGuid(), EscrowId = e.Id, RaisedBy = raisedBy, Reason = reason.Trim(), PriorState = e.State, OpenedAt = now, DueAt = now + DecisionWindow };
    }

    /// <summary>Checked before anything else changes, so a refused decision leaves no half-applied state behind.</summary>
    public void EnsureCanDecide(string reason)
    {
        if (Status != DisputeStatus.Open) throw new DomainValidationException("This dispute was already decided.");
        if (string.IsNullOrWhiteSpace(reason)) throw new DomainValidationException("A written reason is required.");
    }

    public void Decide(DisputeOutcome outcome, Settlement s, string reviewer, string reason, DateTimeOffset now)
    {
        EnsureCanDecide(reason);
        Outcome = outcome; BuyerAmount = s.BuyerRefund; SellerAmount = s.Seller; CourierAmount = s.Courier; PlatformAmount = s.Platform;
        DecidedBy = reviewer; DecisionReason = reason.Trim(); DecidedAt = now; Status = DisputeStatus.Decided;
    }
}

public enum NotificationStatus { Pending = 0, Sent = 1, Dead = 2 }

/// <summary>An SMS waiting to be sent. Created in the same transaction as the change that caused it.</summary>
public sealed class Notification
{
    public const int MaxAttempts = 6;
    private Notification() { ToPhone = ""; Template = ""; Body = ""; }

    public Guid Id { get; private set; }
    public Guid? EscrowId { get; private set; }
    public string ToPhone { get; private set; }
    public string Template { get; private set; }

    /// <summary>Encrypted when <see cref="Sensitive"/> (for example the OTP), and blanked once sent.</summary>
    public string Body { get; private set; }
    public bool Sensitive { get; private set; }
    public NotificationStatus Status { get; private set; }
    public int Attempts { get; private set; }
    public string? ProviderRef { get; private set; }
    public string? LastError { get; private set; }
    public DateTimeOffset NextAttemptAt { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? SentAt { get; private set; }

    public static Notification Create(Guid? escrowId, string phone, string template, string body, bool sensitive, DateTimeOffset now) =>
        new() { Id = Guid.NewGuid(), EscrowId = escrowId, ToPhone = phone, Template = template, Body = body, Sensitive = sensitive, CreatedAt = now, NextAttemptAt = now };

    public void MarkSent(string? providerRef, DateTimeOffset now)
    {
        Status = NotificationStatus.Sent; ProviderRef = providerRef; SentAt = now; LastError = null;
        if (Sensitive) Body = "[redacted]";
    }

    public void MarkFailed(string error, DateTimeOffset now)
    {
        Attempts++; LastError = error;
        if (Attempts >= MaxAttempts) { Status = NotificationStatus.Dead; return; }
        NextAttemptAt = now + TimeSpan.FromSeconds(30 * Math.Pow(2, Attempts));
    }
}

/// <summary>Append-only record of who did what to which thing.</summary>
public sealed class AuditEntry
{
    private AuditEntry() { Actor = ""; Action = ""; EntityType = ""; EntityId = ""; }

    public Guid Id { get; private set; }
    public string Actor { get; private set; }
    public string Action { get; private set; }
    public string EntityType { get; private set; }
    public string EntityId { get; private set; }
    public string? Detail { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public static AuditEntry Create(string actor, string action, string entityType, string entityId, string? detail, DateTimeOffset now) =>
        new() { Id = Guid.NewGuid(), Actor = actor, Action = action, EntityType = entityType, EntityId = entityId, Detail = detail, CreatedAt = now };
}

public enum ReconciliationKind { ProviderOnly = 0, InternalOnly = 1, Mismatch = 2, ClearingBalance = 3, EscrowBalance = 4 }

public sealed class ReconciliationRun
{
    private ReconciliationRun() { }
    public Guid Id { get; private set; }
    public DateOnly ForDate { get; private set; }
    public DateTimeOffset RanAt { get; private set; }
    public int ProviderCount { get; private set; }
    public int MatchedCount { get; private set; }
    public int ExceptionCount { get; private set; }

    public static ReconciliationRun Create(DateOnly day, int providerCount, int matched, int exceptions, DateTimeOffset now) =>
        new() { Id = Guid.NewGuid(), ForDate = day, RanAt = now, ProviderCount = providerCount, MatchedCount = matched, ExceptionCount = exceptions };
}

public sealed class ReconciliationItem
{
    private ReconciliationItem() { Reference = ""; Detail = ""; }
    public Guid Id { get; private set; }
    public Guid RunId { get; private set; }
    public ReconciliationKind Kind { get; private set; }
    public string Reference { get; private set; }
    public string Detail { get; private set; }

    public static ReconciliationItem Create(Guid runId, ReconciliationKind kind, string reference, string detail) =>
        new() { Id = Guid.NewGuid(), RunId = runId, Kind = kind, Reference = reference, Detail = detail };
}
