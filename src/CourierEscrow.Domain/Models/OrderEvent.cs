namespace CourierEscrow.Domain.Models;

/// <summary>Immutable record of one thing that happened to an order. Append-only; unique per (order, client event id).</summary>
public sealed class OrderEvent
{
    private OrderEvent() { Type = ""; FromState = ""; ToState = ""; Actor = ""; }

    public Guid Id { get; private set; }
    public Guid EscrowId { get; private set; }
    public string Type { get; private set; }
    public string FromState { get; private set; }
    public string ToState { get; private set; }
    public string Actor { get; private set; }

    /// <summary>Set for events submitted by a client; makes retries from a weak network harmless.</summary>
    public string? ClientEventId { get; private set; }
    public string? Payload { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public static OrderEvent Create(Guid escrowId, string type, EscrowState from, EscrowState to, string actor,
        string? clientEventId, string? payload, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(), EscrowId = escrowId, Type = type, FromState = from.ToString(), ToState = to.ToString(),
        Actor = actor, ClientEventId = clientEventId, Payload = payload, CreatedAt = now
    };
}
