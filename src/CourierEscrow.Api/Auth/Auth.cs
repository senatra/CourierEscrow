using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using CourierEscrow.Application;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace CourierEscrow.Api.Auth;

public static class Policies
{
    public const string Staff = "staff";          // any signed-in staff member
    public const string Ops = "ops";              // ops_agent, ops_admin
    public const string OpsAdmin = "ops_admin";   // decisions that move money or change accounts
    public const string Finance = "finance";      // ops_admin, finance
    public const string Courier = "courier";
}

/// <summary>
/// Three ways in:
/// <list type="bullet">
/// <item>staff sign in with <c>POST /auth/login</c> and send <c>Authorization: Bearer</c>; the account is checked on every request, so disabling a user or changing their password or role takes effect at once;</item>
/// <item>couriers sign in with <c>POST /courier/login</c> and send <c>Authorization: Bearer</c>;</item>
/// <item><c>X-Api-Key</c> for scripts and CI (keys come from configuration / a secret store; none are configured by default).</item>
/// </list>
/// </summary>
public sealed class EscrowAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder,
    AuthOptions auth, AccessTokens tokens, IStaffUserRepository staff) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string Scheme = "Escrow";

    /// <summary>Present when the signed-in person has to choose a new password before anything else.</summary>
    public const string PasswordChangeClaim = "pwd_change_required";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (Request.Headers.TryGetValue("X-Api-Key", out var key) && !string.IsNullOrWhiteSpace(key))
        {
            var presented = Encoding.UTF8.GetBytes(key.ToString());
            StaffKey? match = null;
            foreach (var candidate in auth.StaffKeys)   // check every key: no early exit that leaks which one was close
            {
                var expected = Encoding.UTF8.GetBytes(candidate.Key);
                if (expected.Length > 0 && CryptographicOperations.FixedTimeEquals(SHA256.HashData(presented), SHA256.HashData(expected))) match = candidate;
            }
            return match is null ? AuthenticateResult.Fail("Invalid API key.") : Ok(match.Name, match.Name, match.Role);
        }

        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return AuthenticateResult.NoResult();

        var principal = tokens.Validate(header["Bearer ".Length..].Trim());
        if (principal is null) return AuthenticateResult.Fail("Invalid or expired token.");
        if (principal.Role == Roles.Courier) return Ok(principal.Subject, principal.Name, principal.Role);

        // A staff token is only as good as the account behind it: it must still exist, be active, and carry the current stamp.
        if (!Roles.IsStaff(principal.Role) || principal.Stamp is null || !Guid.TryParse(principal.Subject, out var userId))
            return AuthenticateResult.Fail("Invalid token.");

        var user = await staff.GetAsync(userId, Context.RequestAborted);
        if (user is null || !user.IsActive || user.SecurityStamp != principal.Stamp)
            return AuthenticateResult.Fail("This session is no longer valid. Sign in again.");

        // The role comes from the database, not the token.
        return Ok(user.Id.ToString(), user.Username, user.Role, user.MustChangePassword);
    }

    private AuthenticateResult Ok(string id, string name, string role, bool mustChangePassword = false)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, id), new(ClaimTypes.Name, name), new(ClaimTypes.Role, role) };
        if (mustChangePassword) claims.Add(new Claim(PasswordChangeClaim, "true"));
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme)), Scheme));
    }
}

/// <summary>
/// A person who still has a temporary password can do exactly two things: see who they are and change it.
/// Everything else is refused until they have.
/// </summary>
public sealed class PasswordChangeGuard(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext ctx)
    {
        if (ctx.User.HasClaim(EscrowAuthHandler.PasswordChangeClaim, "true") && !Allowed(ctx.Request.Path))
            return Results.Problem(statusCode: 403, title: "Password change required",
                detail: "You must choose a new password before you can continue.").ExecuteAsync(ctx);
        return next(ctx);
    }

    private static bool Allowed(PathString path) =>
        path.StartsWithSegments("/auth/change-password") || path.StartsWithSegments("/auth/me");
}

public static class UserExtensions
{
    public static Guid CourierId(this ClaimsPrincipal user) => Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);

    /// <summary>The signed-in staff user's id, or null for an API key (which has no account).</summary>
    public static Guid? StaffUserId(this ClaimsPrincipal user) =>
        !user.IsInRole(Roles.Courier) && Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    /// <summary>Who to write in the audit trail: "staff:alice" or "courier:{id}".</summary>
    public static string Actor(this ClaimsPrincipal user) =>
        user.IsInRole(Roles.Courier) ? Actors.Courier(user.CourierId()) : Actors.Staff(user.FindFirstValue(ClaimTypes.Name) ?? "unknown");
}
