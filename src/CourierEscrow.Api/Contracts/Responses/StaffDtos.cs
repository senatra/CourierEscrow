namespace CourierEscrow.Api;

public sealed record StaffUserDto(
    Guid Id, string Username, string DisplayName, string Role, string Status, bool MustChangePassword, bool Locked,
    DateTimeOffset CreatedAt, DateTimeOffset? LastLoginAt);

public sealed record StaffLoginResponse(string Token, DateTimeOffset ExpiresAt, StaffUserDto User);
