using CourierEscrow.Application;
using CourierEscrow.Application.Services;
using CourierEscrow.Domain;
using CourierEscrow.Domain.Ledger;

namespace CourierEscrow.Api;

// requests 
public sealed record LoginRequest(string Phone, string Pin, string DeviceId);
public sealed record PresignRequest(string ContentType);

/// <summary>One shape for every courier action. <c>type</c> picks which fields matter.</summary>
public sealed record CourierEventRequest(
    string Type, string ClientEventId, List<Guid>? UploadIds = null, decimal? WeightKg = null, string? SealId = null,
    double? Latitude = null, double? Longitude = null, DateTimeOffset? ClientTime = null,
    string? Otp = null, string? ReasonCode = null, bool? SellerConfirmed = null, string? Reason = null);

public sealed record CreateOrderRequest(
    string BuyerName, string BuyerPhone, string? BuyerNetwork,
    string SellerName, string SellerPhone, string? SellerNetwork,
    Guid CourierId, string ItemDescription, long ItemAmount, long DeliveryFee,
    string? Currency = null, string? PickupAddress = null, string? DeliveryAddress = null,
    List<Guid>? ListingUploadIds = null);

public sealed record ReasonRequest(string Reason);
public sealed record DecideRequest(DisputeOutcome Outcome, string Reason, long BuyerAmount = 0, long SellerAmount = 0, long CourierAmount = 0, long PlatformAmount = 0);
public sealed record RetryPayoutRequest(bool NewKey = false, string? Msisdn = null, string? Network = null);
public sealed record CreateCourierRequest(string Name, string Phone, string Network, string Pin);
public sealed record CourierStatusRequest(bool Active);

//  responses
public sealed record OrderSummaryDto(
    Guid Id, string OrderNumber, string State, string ItemDescription, long ItemAmount, long DeliveryFee, long PlatformFee, long Total,
    string Currency, string BuyerName, string BuyerPhone, string SellerName, string SellerPhone, string? OpsFlag, DateTimeOffset CreatedAt,
    string TrackingToken, DateTimeOffset? ProductConfirmedAt);

public sealed record JobDto(
    Guid Id, string OrderNumber, string State, string ItemDescription, long DeliveryFee, string Currency,
    string? PickupAddress, string? DeliveryAddress, string SellerName, string SellerPhone, string BuyerName, string BuyerPhone,
    string? SealId, string NextAction, IReadOnlyList<Guid> ListingPhotoIds);

public sealed record PaymentDto(string Reference, string? AccountNumber, DateTimeOffset ExpiresAt, string State);
public sealed record EventDto(string Type, string From, string To, string Actor, string? ClientEventId, string? Payload, DateTimeOffset At);
public sealed record ProofDto(Guid Id, string Kind, string Sha256, double? Latitude, double? Longitude, DateTimeOffset ServerTime, DateTimeOffset? ClientTime);
public sealed record LedgerEntryDto(string Account, string Direction, long Amount);
public sealed record LedgerTxDto(Guid Id, Guid EscrowId, string Kind, DateTimeOffset At, IReadOnlyList<LedgerEntryDto> Entries);
public sealed record PayoutDto(Guid Id, Guid EscrowId, string Kind, string Beneficiary, string Wallet, string? Network, long Amount, string Currency, string Reference, string Status, int Attempts, string? LastError, long Fee, DateTimeOffset CreatedAt);
public sealed record IntentDto(string Reference, string Status, long Amount, DateTimeOffset ExpiresAt);
public sealed record DisputeDto(Guid Id, Guid EscrowId, string RaisedBy, string Reason, string Status, string PriorState, DateTimeOffset OpenedAt, DateTimeOffset DueAt, string? Outcome, long BuyerAmount, long SellerAmount, long CourierAmount, long PlatformAmount, string? DecidedBy, string? DecisionReason);
public sealed record OrderDetailDto(
    OrderSummaryDto Order, string? CourierName, string? SealId, int? WeightGrams, string? OtpStatus, int OtpAttempts,
    IReadOnlyList<EventDto> Events, IReadOnlyList<ProofDto> Proofs, IReadOnlyList<LedgerTxDto> Ledger,
    IReadOnlyList<PayoutDto> Payouts, IReadOnlyList<IntentDto> Payments, IReadOnlyList<DisputeDto> Disputes, long HeldInEscrow);
public sealed record CourierDto(Guid Id, string Name, string Phone, string Network, string Status, bool DeviceBound);
public sealed record SummaryDto(LedgerSummary Ledger, AlertSummary Alerts);
public sealed record ReconciliationRunDto(Guid Id, string ForDate, DateTimeOffset RanAt, int ProviderCount, int MatchedCount, int ExceptionCount);
public sealed record ReconciliationItemDto(string Kind, string Reference, string Detail);

public static class Mapping
{
    private static string? Mask(string? msisdn) => msisdn is null ? null : PhoneNumber.Mask(msisdn);

    /// <summary>Phone numbers are masked in the admin API. Couriers get full numbers only for their own jobs.</summary>
    public static OrderSummaryDto ToDto(this Escrow e, Party buyer, Party seller) => new(
        e.Id, e.OrderNumber, e.State.ToString(), e.ItemDescription, e.ItemAmount, e.DeliveryFee, e.PlatformFee, e.Total, e.Currency,
        buyer.Name, Mask(buyer.Phone)!, seller.Name, Mask(seller.Phone)!, e.OpsFlag, e.CreatedAt, e.TrackingToken, e.ProductConfirmedAt);

    public static JobDto ToDto(this CourierJob j, IEnumerable<Proof>? proofs = null) => new(
        j.Escrow.Id, j.Escrow.OrderNumber, j.Escrow.State.ToString(), j.Escrow.ItemDescription, j.Escrow.DeliveryFee, j.Escrow.Currency,
        j.Escrow.PickupAddress, j.Escrow.DeliveryAddress, j.Seller.Name, "+" + j.Seller.Phone, j.Buyer.Name, "+" + j.Buyer.Phone,
        j.Escrow.SealId, j.Escrow.State switch
        {
            EscrowState.Funded => "pickup",
            EscrowState.PickedUp => "arrive",
            EscrowState.Delivered => "otp",
            EscrowState.Rejected => "return",
            _ => "none"
        }, (proofs ?? []).Where(p => p.Kind == ProofKind.Listing).Select(p => p.Id).ToList());

    public static PayoutDto ToDto(this Payout p) => new(
        p.Id, p.EscrowId, p.Kind.ToString(), p.BeneficiaryName, Mask(p.Msisdn)!, p.Network?.ToString(), p.Amount, p.Currency, p.Reference,
        p.Status.ToString(), p.Attempts, p.LastError, p.Fee, p.CreatedAt);

    public static DisputeDto ToDto(this Dispute d) => new(
        d.Id, d.EscrowId, d.RaisedBy, d.Reason, d.Status.ToString(), d.PriorState.ToString(), d.OpenedAt, d.DueAt, d.Outcome?.ToString(),
        d.BuyerAmount, d.SellerAmount, d.CourierAmount, d.PlatformAmount, d.DecidedBy, d.DecisionReason);

    public static LedgerTxDto ToDto(this LedgerTransaction t) => new(
        t.Id, t.EscrowId, t.Kind, t.CreatedAt, t.Entries.Select(x => new LedgerEntryDto(x.Account, x.Direction.ToString(), x.Amount)).ToList());

    public static CourierDto ToDto(this CourierAccount c) => new(c.Id, c.Name, Mask(c.Phone)!, c.WalletNetwork.ToString(), c.Status.ToString(), c.DeviceId is not null);

    public static OrderDetailDto ToDto(this OrderDetail d, long held) => new(
        d.Escrow.ToDto(d.Buyer, d.Seller), d.Courier?.Name, d.Escrow.SealId, d.Escrow.WeightGrams, d.Otp?.Status.ToString(), d.Otp?.Attempts ?? 0,
        d.Events.Select(e => new EventDto(e.Type, e.FromState, e.ToState, e.Actor, e.ClientEventId, e.Payload, e.CreatedAt)).ToList(),
        d.Proofs.Select(p => new ProofDto(p.Id, p.Kind.ToString(), p.Sha256, p.Latitude, p.Longitude, p.ServerTime, p.ClientTime)).ToList(),
        d.Ledger.Select(t => t.ToDto()).ToList(),
        d.Payouts.Select(p => p.ToDto()).ToList(),
        d.Intents.Select(i => new IntentDto(i.Reference, i.Status.ToString(), i.Amount, i.ExpiresAt)).ToList(),
        d.Disputes.Select(x => x.ToDto()).ToList(), held);
}
