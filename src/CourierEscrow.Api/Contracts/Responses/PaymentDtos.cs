namespace CourierEscrow.Api;

public sealed record PaymentDto(string Reference, string? AccountNumber, DateTimeOffset ExpiresAt, string State);
public sealed record PayoutDto(Guid Id, Guid EscrowId, string Kind, string Beneficiary, string Wallet, string? Network, long Amount, string Currency, string Reference, string Status, int Attempts, string? LastError, long Fee, DateTimeOffset CreatedAt);
public sealed record IntentDto(string Reference, string Status, long Amount, DateTimeOffset ExpiresAt);
