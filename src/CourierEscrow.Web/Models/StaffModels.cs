namespace CourierEscrow.Web.Models;

public sealed record StaffUserRow(Guid Id, string Username, string DisplayName, string Role, string Status, bool MustChangePassword, bool Locked, DateTimeOffset CreatedAt, DateTimeOffset? LastLoginAt);
public sealed record LoginResponse(string Token, DateTimeOffset ExpiresAt, StaffUserRow User);
public sealed record CourierRow(Guid Id, string Name, string Phone, string Network, string Status, bool DeviceBound);
