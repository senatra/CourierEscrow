using CourierEscrow.Domain.Models;

namespace CourierEscrow.Api;

public sealed record DecideRequest(DisputeOutcome Outcome, string Reason, long BuyerAmount = 0, long SellerAmount = 0, long CourierAmount = 0, long PlatformAmount = 0);
public sealed record RetryPayoutRequest(bool NewKey = false, string? Msisdn = null, string? Network = null);
