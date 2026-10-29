namespace CourierEscrow.Api;

public sealed record OrderSummaryDto(
    Guid Id, string OrderNumber, string State, string ItemDescription, long ItemAmount, long DeliveryFee, long PlatformFee, long Total,
    string Currency, string BuyerName, string BuyerPhone, string SellerName, string SellerPhone, string? OpsFlag, DateTimeOffset CreatedAt,
    string TrackingToken, DateTimeOffset? ProductConfirmedAt);

public sealed record JobDto(
    Guid Id, string OrderNumber, string State, string ItemDescription, long DeliveryFee, string Currency,
    string? PickupAddress, string? DeliveryAddress, string SellerName, string SellerPhone, string BuyerName, string BuyerPhone,
    string? SealId, string NextAction, IReadOnlyList<Guid> ListingPhotoIds);

public sealed record EventDto(string Type, string From, string To, string Actor, string? ClientEventId, string? Payload, DateTimeOffset At);
public sealed record ProofDto(Guid Id, string Kind, string Sha256, double? Latitude, double? Longitude, DateTimeOffset ServerTime, DateTimeOffset? ClientTime);
public sealed record OrderDetailDto(
    OrderSummaryDto Order, string? CourierName, string? SealId, int? WeightGrams, string? OtpStatus, int OtpAttempts,
    IReadOnlyList<EventDto> Events, IReadOnlyList<ProofDto> Proofs, IReadOnlyList<LedgerTxDto> Ledger,
    IReadOnlyList<PayoutDto> Payouts, IReadOnlyList<IntentDto> Payments, IReadOnlyList<DisputeDto> Disputes, long HeldInEscrow);
