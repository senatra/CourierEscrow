namespace CourierEscrow.Api;

public sealed record CourierEventRequest(
    string Type, string ClientEventId, List<Guid>? UploadIds = null, decimal? WeightKg = null, string? SealId = null,
    double? Latitude = null, double? Longitude = null, DateTimeOffset? ClientTime = null,
    string? Otp = null, string? ReasonCode = null, bool? SellerConfirmed = null, string? Reason = null);

public sealed record CreateCourierRequest(string Name, string Phone, string Network, string Pin);
public sealed record CourierStatusRequest(bool Active);
