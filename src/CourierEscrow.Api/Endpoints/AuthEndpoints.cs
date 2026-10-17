using System.Security.Claims;
using CourierEscrow.Api.Auth;
using CourierEscrow.Application;
using CourierEscrow.Application.Services;
using CourierEscrow.Domain;
using Microsoft.AspNetCore.RateLimiting;

namespace CourierEscrow.Api.Endpoints;

/// <summary>Sign-in for staff, and the signed-in person's own account.</summary>
public static class AuthEndpoints
{
    public const string LoginRateLimit = "login";

    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/auth").WithTags("Auth");

        g.MapPost("/login", Login).AllowAnonymous().RequireRateLimiting(LoginRateLimit);
        g.MapGet("/me", Me).RequireAuthorization(Policies.Staff);
        g.MapPost("/change-password", ChangePassword).RequireAuthorization(Policies.Staff);
    }

    private static async Task<IResult> Login(StaffLoginRequest r, UserService users, TimeProvider clock, CancellationToken ct) =>
        Results.Ok((await users.LoginAsync(r.Username, r.Password, ct)).ToDto(clock.GetUtcNow()));

    private static async Task<IResult> Me(HttpContext http, UserService users, TimeProvider clock, CancellationToken ct)
    {
        // An API key has no account behind it.
        if (http.User.StaffUserId() is not { } id)
            return Results.Ok(new { username = http.User.FindFirstValue(ClaimTypes.Name), role = http.User.FindFirstValue(ClaimTypes.Role), apiKey = true });

        return Results.Ok((await users.GetAsync(id, ct)).ToDto(clock.GetUtcNow()));
    }

    private static async Task<IResult> ChangePassword(ChangePasswordRequest r, HttpContext http, UserService users, TimeProvider clock, CancellationToken ct)
    {
        if (http.User.StaffUserId() is not { } id) throw new DomainValidationException("API keys have no password.");
        return Results.Ok((await users.ChangePasswordAsync(id, r.CurrentPassword, r.NewPassword, ct)).ToDto(clock.GetUtcNow()));
    }
}
