using CourierEscrow.Api.Auth;
using CourierEscrow.Application.Services;

namespace CourierEscrow.Api.Endpoints;

/// <summary>Managing staff accounts. Administrators only (ops_admin).</summary>
public static class UserEndpoints
{
    public static void MapUserEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/admin/users").WithTags("Users").RequireAuthorization(Policies.OpsAdmin);

        g.MapGet("", List);
        g.MapPost("", Create);
        g.MapPost("/{id:guid}/profile", Rename);
        g.MapPost("/{id:guid}/role", ChangeRole);
        g.MapPost("/{id:guid}/status", SetStatus);
        g.MapPost("/{id:guid}/reset-password", ResetPassword);
        g.MapPost("/{id:guid}/unlock", Unlock);
    }

    private static async Task<IResult> List(UserService users, TimeProvider clock, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        return Results.Ok((await users.ListAsync(ct)).Select(u => u.ToDto(now)));
    }

    private static async Task<IResult> Create(CreateStaffUserRequest r, HttpContext http, UserService users, TimeProvider clock, CancellationToken ct)
    {
        var u = await users.CreateAsync(r.Username, r.DisplayName, r.Role, r.Password, http.User.Actor(), ct);
        return Results.Created($"/admin/users/{u.Id}", u.ToDto(clock.GetUtcNow()));
    }

    private static async Task<IResult> Rename(Guid id, StaffProfileRequest r, HttpContext http, UserService users, TimeProvider clock, CancellationToken ct) =>
        Results.Ok((await users.RenameAsync(id, r.DisplayName, http.User.Actor(), ct)).ToDto(clock.GetUtcNow()));

    private static async Task<IResult> ChangeRole(Guid id, StaffRoleRequest r, HttpContext http, UserService users, TimeProvider clock, CancellationToken ct) =>
        Results.Ok((await users.ChangeRoleAsync(id, r.Role, http.User.StaffUserId(), http.User.Actor(), ct)).ToDto(clock.GetUtcNow()));

    private static async Task<IResult> SetStatus(Guid id, StaffStatusRequest r, HttpContext http, UserService users, TimeProvider clock, CancellationToken ct) =>
        Results.Ok((await users.SetActiveAsync(id, r.Active, http.User.StaffUserId(), http.User.Actor(), ct)).ToDto(clock.GetUtcNow()));

    private static async Task<IResult> ResetPassword(Guid id, ResetPasswordRequest r, HttpContext http, UserService users, TimeProvider clock, CancellationToken ct) =>
        Results.Ok((await users.ResetPasswordAsync(id, r.NewPassword, http.User.StaffUserId(), http.User.Actor(), ct)).ToDto(clock.GetUtcNow()));

    private static async Task<IResult> Unlock(Guid id, HttpContext http, UserService users, TimeProvider clock, CancellationToken ct) =>
        Results.Ok((await users.UnlockAsync(id, http.User.Actor(), ct)).ToDto(clock.GetUtcNow()));
}
