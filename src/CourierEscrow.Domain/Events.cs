namespace CourierEscrow.Domain;

/// <summary>
/// Facts about an order that other parts of the system (analytics, partners, dashboards) care about.
/// They are written to the outbox in the same DB transaction as the state change.
/// </summary>
public abstract record DomainEvent(Guid EscrowId, DateTimeOffset OccurredAt)
{
    /// <summary>Unique per event; consumers use it as their idempotency key.</summary>
    public Guid EventId { get; init; } = Guid.NewGuid();
    public string Type => GetType().Name;
}

public sealed record EscrowCreated(Guid EscrowId, DateTimeOffset OccurredAt, string OrderNumber,
    Guid BuyerId, Guid SellerId, Guid CourierId, long Total, string Currency) : DomainEvent(EscrowId, OccurredAt);

public sealed record EscrowProductConfirmed(Guid EscrowId, DateTimeOffset OccurredAt) : DomainEvent(EscrowId, OccurredAt);
public sealed record EscrowPaymentRequested(Guid EscrowId, DateTimeOffset OccurredAt, string Reference) : DomainEvent(EscrowId, OccurredAt);
public sealed record EscrowFunded(Guid EscrowId, DateTimeOffset OccurredAt, long Amount, string Currency) : DomainEvent(EscrowId, OccurredAt);
public sealed record EscrowPickedUp(Guid EscrowId, DateTimeOffset OccurredAt, string SealId, int WeightGrams) : DomainEvent(EscrowId, OccurredAt);
public sealed record EscrowDelivered(Guid EscrowId, DateTimeOffset OccurredAt) : DomainEvent(EscrowId, OccurredAt);

/// <summary>Funds moved to payable accounts. The payout worker reacts to the payout rows created in the same transaction.</summary>
public sealed record EscrowReleased(Guid EscrowId, DateTimeOffset OccurredAt, Guid SellerId, long SellerAmount,
    Guid CourierId, long CourierAmount, long PlatformAmount, string Currency) : DomainEvent(EscrowId, OccurredAt);

public sealed record EscrowRejected(Guid EscrowId, DateTimeOffset OccurredAt, string ReasonCode) : DomainEvent(EscrowId, OccurredAt);
public sealed record EscrowReturned(Guid EscrowId, DateTimeOffset OccurredAt) : DomainEvent(EscrowId, OccurredAt);

public sealed record EscrowRefunded(Guid EscrowId, DateTimeOffset OccurredAt, Guid BuyerId, long BuyerAmount,
    long CourierAmount, string Currency) : DomainEvent(EscrowId, OccurredAt);

public sealed record EscrowDisputed(Guid EscrowId, DateTimeOffset OccurredAt, string FromState) : DomainEvent(EscrowId, OccurredAt);
public sealed record EscrowSettled(Guid EscrowId, DateTimeOffset OccurredAt) : DomainEvent(EscrowId, OccurredAt);
public sealed record EscrowCancelled(Guid EscrowId, DateTimeOffset OccurredAt, string Reason) : DomainEvent(EscrowId, OccurredAt);
public sealed record EscrowFlagged(Guid EscrowId, DateTimeOffset OccurredAt, string Reason) : DomainEvent(EscrowId, OccurredAt);
