using System.Text.Json;

namespace CourierEscrow.Domain.Models;

public enum EscrowState
{
    Created = 0,
    PaymentPending = 1,
    Funded = 2,
    PickedUp = 3,
    Delivered = 4,
    Released = 5,
    Settled = 6,
    Rejected = 7,
    Returned = 8,
    Refunded = 9,
    Disputed = 10,
    Cancelled = 11
}

public sealed record PickupEvidence(int PhotoCount, string? SealId, decimal WeightKg, GeoPoint? Gps);
public sealed record ArrivalEvidence(int PhotoCount, GeoPoint? Gps);

/// <summary>
/// One order's escrow. Money is in minor units (tambala) as <see cref="long"/>; never floating point.
///
///  Created -> PaymentPending -> Funded -> PickedUp -> Delivered -> Released -> Settled
///     |            |             |                       |  \
///     +-Cancelled--+             |                       |   +-> Rejected -> Returned -> Refunded -> Settled
///                                +-> Refunded (cancelled after payment, before pickup)
///   Funded..Returned -> Disputed -> Released | Refunded      (never once Released)
///
/// The aggregate only guards its own state machine, and every change appends an <see cref="OrderEvent"/>.
/// Moving money is done by posting balanced ledger transactions in the same unit of work.
/// </summary>
public sealed class Escrow
{
    public const int MinListingPhotos = 3;
    public const int MinPickupPhotos = 3;
    public const int MinArrivalPhotos = 1;
    public const int MinRejectionPhotos = 2;

    private readonly List<DomainEvent> _events = [];
    private readonly List<OrderEvent> _orderEvents = [];

    private Escrow() { OrderNumber = ""; ItemDescription = ""; TrackingToken = ""; }

    public Guid Id { get; private set; }
    public string OrderNumber { get; private set; }
    public Guid BuyerId { get; private set; }
    public Guid SellerId { get; private set; }
    public Guid CourierId { get; private set; }
    public string ItemDescription { get; private set; }
    public string? PickupAddress { get; private set; }
    public string? DeliveryAddress { get; private set; }
    public string Currency { get; private set; } = "MWK";

    public long ItemAmount { get; private set; }
    public long DeliveryFee { get; private set; }

    /// <summary>Platform fee rate in basis points, fixed at creation so past orders never change.</summary>
    public int FeeBps { get; private set; }
    public long PlatformFee { get; private set; }

    /// <summary>What the buyer pays and the escrow holds.</summary>
    public long Total => ItemAmount + DeliveryFee;
    public long SellerProceeds => ItemAmount - PlatformFee;

    public EscrowState State { get; private set; }
    public EscrowState? DisputedFrom { get; private set; }
    public string? PaymentReference { get; private set; }
    public string? SealId { get; private set; }
    public int? WeightGrams { get; private set; }
    public string TrackingToken { get; private set; }

    /// <summary>Set when something needs a human (wrong amount, OTP lockout, rejection, unreachable buyer).</summary>
    public string? OpsFlag { get; private set; }

    /// <summary>Set when the buyer confirms the listing photos are the item they are buying.</summary>
    public DateTimeOffset? ProductConfirmedAt { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? FundedAt { get; private set; }
    public DateTimeOffset? PickedUpAt { get; private set; }
    public DateTimeOffset? DeliveredAt { get; private set; }
    public DateTimeOffset? ClosedAt { get; private set; }
    public DateTimeOffset? SettledAt { get; private set; }

    /// <summary>Optimistic-concurrency token: bumped on every change, checked on every write.</summary>
    public int Version { get; private set; }

    public IReadOnlyList<DomainEvent> PendingEvents => _events;
    public IReadOnlyList<OrderEvent> PendingOrderEvents => _orderEvents;
    public void ClearEvents() { _events.Clear(); _orderEvents.Clear(); }

    public static Escrow Create(
        string orderNumber, Guid buyerId, Guid sellerId, Guid courierId, string itemDescription,
        long itemAmount, long deliveryFee, int feeBps, string currency,
        string? pickupAddress, string? deliveryAddress, string actor, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(orderNumber)) throw new DomainValidationException("Order number is required.");
        if (string.IsNullOrWhiteSpace(itemDescription)) throw new DomainValidationException("Item description is required.");
        if (itemAmount <= 0) throw new DomainValidationException("Item amount must be greater than zero.");
        if (deliveryFee < 0) throw new DomainValidationException("Delivery fee cannot be negative.");
        if (feeBps is < 0 or >= 10_000) throw new DomainValidationException("Platform fee must be between 0 and 9999 basis points.");
        if (buyerId == sellerId) throw new DomainValidationException("Buyer and seller must be different parties.");
        if (string.IsNullOrWhiteSpace(currency) || currency.Trim().Length != 3)
            throw new DomainValidationException("Currency must be a 3-letter code.");

        var e = new Escrow
        {
            Id = Guid.NewGuid(), OrderNumber = orderNumber.Trim(), BuyerId = buyerId, SellerId = sellerId, CourierId = courierId,
            ItemDescription = itemDescription.Trim(), PickupAddress = Clean(pickupAddress), DeliveryAddress = Clean(deliveryAddress),
            ItemAmount = itemAmount, DeliveryFee = deliveryFee, FeeBps = feeBps,
            PlatformFee = checked(itemAmount * feeBps / 10_000),
            Currency = currency.Trim().ToUpperInvariant(), State = EscrowState.Created,
            TrackingToken = References.TrackingToken(), CreatedAt = now
        };
        e.Log("Created", EscrowState.Created, EscrowState.Created, actor, null, new { e.OrderNumber, e.ItemAmount, e.DeliveryFee, e.FeeBps }, now);
        e._events.Add(new EscrowCreated(e.Id, now, e.OrderNumber, buyerId, sellerId, courierId, e.Total, e.Currency));
        return e;
    }

    public void StartPayment(string reference, string actor, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(reference)) throw new DomainValidationException("Payment reference is required.");
        if (ProductConfirmedAt is null) throw new DomainValidationException("The buyer must confirm the product photos before payment is requested.");
        Move(EscrowState.PaymentPending, "PaymentRequested", actor, now, null, new { reference }, EscrowState.Created);
        PaymentReference = reference;
        _events.Add(new EscrowPaymentRequested(Id, now, reference));
    }

    /// <summary>Buyer looked at the three listing photos and agrees that is the item they are buying.</summary>
    public void ConfirmProduct(string actor, DateTimeOffset now)
    {
        if (ProductConfirmedAt is not null) return;
        if (State != EscrowState.Created) throw new InvalidEscrowTransitionException(State, "confirm the product");
        ProductConfirmedAt = now;
        if (OpsFlag is not null && OpsFlag.StartsWith("Buyer says the photos", StringComparison.Ordinal))
            OpsFlag = null;
        Log("ProductConfirmed", State, State, actor, null, null, now);
        Version++;
        _events.Add(new EscrowProductConfirmed(Id, now));
    }

    /// <summary>Buyer says the listing photos are not the item. No money has moved; ops cancel or replace photos.</summary>
    public void DeclineProduct(string reason, string actor, DateTimeOffset now)
    {
        if (ProductConfirmedAt is not null) throw new DomainValidationException("The buyer already confirmed this item.");
        if (State != EscrowState.Created) throw new InvalidEscrowTransitionException(State, "decline the product");
        if (string.IsNullOrWhiteSpace(reason)) throw new DomainValidationException("A reason is required.");
        FlagForOps($"Buyer says the photos are not the item they are buying: {reason.Trim()}", actor, now);
    }

    public void MarkFunded(string actor, DateTimeOffset now, string? eventKey = null)
    {
        Move(EscrowState.Funded, "PaymentConfirmed", actor, now, null, new { eventKey, amount = Total }, EscrowState.PaymentPending);
        FundedAt = now;
        _events.Add(new EscrowFunded(Id, now, Total, Currency));
    }

    /// <summary>Payment failed or expired, or ops cancelled before any money was taken.</summary>
    public void Cancel(string reason, string actor, DateTimeOffset now)
    {
        Move(EscrowState.Cancelled, "Cancelled", actor, now, null, new { reason }, EscrowState.Created, EscrowState.PaymentPending);
        ClosedAt = now;
        _events.Add(new EscrowCancelled(Id, now, reason));
    }

    public void MarkPickedUp(PickupEvidence ev, string actor, DateTimeOffset now, string? clientEventId = null)
    {
        if (ev.PhotoCount < MinPickupPhotos) throw new DomainValidationException($"At least {MinPickupPhotos} photos of the item are required.");
        if (string.IsNullOrWhiteSpace(ev.SealId)) throw new DomainValidationException("The tamper seal ID is required.");
        if (ev.WeightKg <= 0) throw new DomainValidationException("The item weight is required.");
        if (ev.Gps is null) throw new DomainValidationException("GPS position is required.");
        ev.Gps.Validate();

        var grams = (int)Math.Round(ev.WeightKg * 1000m);
        Move(EscrowState.PickedUp, "PickedUp", actor, now, clientEventId, new { seal = ev.SealId!.Trim(), grams, ev.PhotoCount, ev.Gps }, EscrowState.Funded);
        SealId = ev.SealId.Trim();
        WeightGrams = grams;
        PickedUpAt = now;
        _events.Add(new EscrowPickedUp(Id, now, SealId, grams));
    }

    public void MarkArrived(ArrivalEvidence ev, string actor, DateTimeOffset now, string? clientEventId = null)
    {
        if (ev.PhotoCount < MinArrivalPhotos) throw new DomainValidationException("An arrival photo is required.");
        if (ev.Gps is null) throw new DomainValidationException("GPS position is required.");
        ev.Gps.Validate();

        Move(EscrowState.Delivered, "Arrived", actor, now, clientEventId, new { ev.PhotoCount, ev.Gps }, EscrowState.PickedUp);
        DeliveredAt = now;
        _events.Add(new EscrowDelivered(Id, now));
    }

    /// <summary>Buyer's one-time code was accepted. Call only after the OTP check succeeded.</summary>
    public Settlement Release(string actor, DateTimeOffset now, string? clientEventId = null)
    {
        var s = Settlement.ForRelease(this);
        Move(EscrowState.Released, "OtpAccepted", actor, now, clientEventId, new { s.Seller, s.Courier, s.Platform }, EscrowState.Delivered);
        ClosedAt = now;
        _events.Add(new EscrowReleased(Id, now, SellerId, s.Seller, CourierId, s.Courier, s.Platform, Currency));
        return s;
    }

    public void Reject(string reasonCode, int photoCount, GeoPoint? gps, string actor, DateTimeOffset now, string? clientEventId = null)
    {
        if (string.IsNullOrWhiteSpace(reasonCode)) throw new DomainValidationException("A rejection reason is required.");
        if (photoCount < MinRejectionPhotos) throw new DomainValidationException($"At least {MinRejectionPhotos} photos are required to reject.");
        if (gps is null) throw new DomainValidationException("GPS position is required.");

        Move(EscrowState.Rejected, "Rejected", actor, now, clientEventId, new { reasonCode, photoCount, gps }, EscrowState.Delivered);
        OpsFlag = $"Rejected at the door: {reasonCode.Trim()}";
        _events.Add(new EscrowRejected(Id, now, reasonCode.Trim()));
    }

    public void ConfirmReturn(int photoCount, bool sellerConfirmed, string actor, DateTimeOffset now, string? clientEventId = null)
    {
        if (photoCount < 1) throw new DomainValidationException("A return photo is required.");
        if (!sellerConfirmed) throw new DomainValidationException("The seller must confirm the return.");

        Move(EscrowState.Returned, "Returned", actor, now, clientEventId, new { photoCount }, EscrowState.Rejected);
        OpsFlag = "Returned to seller: awaiting refund decision";
        _events.Add(new EscrowReturned(Id, now));
    }

    /// <summary>Money goes back to the buyer. Allowed after a return, or after a cancel that happens between payment and pickup.</summary>
    public void Refund(Settlement s, string actor, DateTimeOffset now)
    {
        s.EnsureMatches(this);
        if (s.Seller > 0 || s.Platform > 0) throw new DomainValidationException("A refund cannot pay the seller or the platform.");
        Move(EscrowState.Refunded, "Refunded", actor, now, null, new { s.BuyerRefund, s.Courier }, EscrowState.Returned, EscrowState.Funded);
        Close(now);
        _events.Add(new EscrowRefunded(Id, now, BuyerId, s.BuyerRefund, s.Courier, Currency));
    }

    public void Settle(string actor, DateTimeOffset now)
    {
        Move(EscrowState.Settled, "Settled", actor, now, null, null, EscrowState.Released, EscrowState.Refunded);
        SettledAt = now;
        _events.Add(new EscrowSettled(Id, now));
    }

    public void OpenDispute(string actor, DateTimeOffset now)
    {
        var from = State;
        Move(EscrowState.Disputed, "DisputeOpened", actor, now, null, new { from = from.ToString() },
            EscrowState.Funded, EscrowState.PickedUp, EscrowState.Delivered, EscrowState.Rejected, EscrowState.Returned);
        DisputedFrom = from;
        OpsFlag = "Dispute open";
        _events.Add(new EscrowDisputed(Id, now, from.ToString()));
    }

    /// <summary>A reviewer decided. Pays the seller (Released) or sends the money back (Refunded) or both.</summary>
    public void ResolveDispute(Settlement s, string actor, DateTimeOffset now)
    {
        s.EnsureMatches(this);
        var to = s.Seller > 0 ? EscrowState.Released : EscrowState.Refunded;
        Move(to, "DisputeDecided", actor, now, null, new { s.BuyerRefund, s.Seller, s.Courier, s.Platform }, EscrowState.Disputed);
        Close(now);
        if (to == EscrowState.Released)
            _events.Add(new EscrowReleased(Id, now, SellerId, s.Seller, CourierId, s.Courier, s.Platform, Currency));
        else
            _events.Add(new EscrowRefunded(Id, now, BuyerId, s.BuyerRefund, s.Courier, Currency));
    }

    public void FlagForOps(string reason, string actor, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new DomainValidationException("A reason is required.");
        OpsFlag = reason.Trim();
        Log("Flagged", State, State, actor, null, new { reason = OpsFlag }, now);
        Version++;
        _events.Add(new EscrowFlagged(Id, now, OpsFlag));
    }

    public void ClearOpsFlag(string actor, DateTimeOffset now)
    {
        if (OpsFlag is null) return;
        OpsFlag = null;
        Log("FlagCleared", State, State, actor, null, null, now);
        Version++;
    }

    /// <summary>Non-state event (for example a failed OTP attempt) so client retries can be recognised.</summary>
    public void RecordEvent(string type, string actor, DateTimeOffset now, string? clientEventId, object? payload = null)
    {
        Log(type, State, State, actor, clientEventId, payload, now);
        Version++;
    }

    private void Close(DateTimeOffset now) => ClosedAt = now;

    private void Move(EscrowState to, string type, string actor, DateTimeOffset now, string? clientEventId, object? payload, params EscrowState[] allowedFrom)
    {
        if (Array.IndexOf(allowedFrom, State) < 0) throw new InvalidEscrowTransitionException(State, type);
        var from = State;
        State = to;
        Version++;
        Log(type, from, to, actor, clientEventId, payload, now);
    }

    private void Log(string type, EscrowState from, EscrowState to, string actor, string? clientEventId, object? payload, DateTimeOffset now) =>
        _orderEvents.Add(OrderEvent.Create(Id, type, from, to, actor, clientEventId,
            payload is null ? null : JsonSerializer.Serialize(payload), now));

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
