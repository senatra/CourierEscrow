using CourierEscrow.Application.Services;
using CourierEscrow.Domain.Models;

namespace CourierEscrow.Api;

public static class StaffMapping
{
    public static StaffUserDto ToDto(this StaffUser u, DateTimeOffset now) =>
        new(u.Id, u.Username, u.DisplayName, u.Role, u.Status.ToString(), u.MustChangePassword, u.IsLocked(now), u.CreatedAt, u.LastLoginAt);

    public static StaffLoginResponse ToDto(this StaffSession s, DateTimeOffset now) => new(s.Token, s.ExpiresAt, s.User.ToDto(now));
}
