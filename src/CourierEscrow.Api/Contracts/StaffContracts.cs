using CourierEscrow.Application.Services;
using CourierEscrow.Domain;

namespace CourierEscrow.Api;

public sealed record StaffLoginRequest(string Username, string Password);
public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);
public sealed record CreateStaffUserRequest(string Username, string DisplayName, string Role, string Password);
public sealed record StaffProfileRequest(string DisplayName);
public sealed record StaffRoleRequest(string Role);
public sealed record StaffStatusRequest(bool Active);
public sealed record ResetPasswordRequest(string NewPassword);

public sealed record StaffUserDto(
    Guid Id, string Username, string DisplayName, string Role, string Status, bool MustChangePassword, bool Locked,
    DateTimeOffset CreatedAt, DateTimeOffset? LastLoginAt);

public sealed record StaffLoginResponse(string Token, DateTimeOffset ExpiresAt, StaffUserDto User);

public static class StaffMapping
{
    public static StaffUserDto ToDto(this StaffUser u, DateTimeOffset now) =>
        new(u.Id, u.Username, u.DisplayName, u.Role, u.Status.ToString(), u.MustChangePassword, u.IsLocked(now), u.CreatedAt, u.LastLoginAt);

    public static StaffLoginResponse ToDto(this StaffSession s, DateTimeOffset now) => new(s.Token, s.ExpiresAt, s.User.ToDto(now));
}
