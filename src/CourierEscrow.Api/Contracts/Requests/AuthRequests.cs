namespace CourierEscrow.Api;

public sealed record LoginRequest(string Phone, string Pin, string DeviceId);
