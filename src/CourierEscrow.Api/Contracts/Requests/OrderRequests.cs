namespace CourierEscrow.Api;

public sealed record CreateOrderRequest(
    string BuyerName, string BuyerPhone, string? BuyerNetwork,
    string SellerName, string SellerPhone, string? SellerNetwork,
    Guid CourierId, string ItemDescription, long ItemAmount, long DeliveryFee,
    string? Currency = null, string? PickupAddress = null, string? DeliveryAddress = null,
    List<Guid>? ListingUploadIds = null);

public sealed record ReasonRequest(string Reason);
