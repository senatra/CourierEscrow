using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace CourierEscrow.Web.Services;

/// <summary>
/// The console's sign-in is an encrypted cookie. It carries the person's identity and the access token the API issued them,
/// and expires when that token does. The browser never sees the token.
/// </summary>
public static class ConsoleSession
{
    public const string TokenClaim = "api_token";
    public const string DisplayNameClaim = "display_name";
    public const string PasswordChangeClaim = "pwd_change";

    public static Task SignInAsync(HttpContext http, LoginResponse login)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, login.User.Id.ToString()),
            new(ClaimTypes.Name, login.User.Username),
            new(ClaimTypes.Role, login.User.Role),
            new(DisplayNameClaim, login.User.DisplayName),
            new(TokenClaim, login.Token)
        };
        if (login.User.MustChangePassword) claims.Add(new Claim(PasswordChangeClaim, "true"));

        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme, ClaimTypes.Name, ClaimTypes.Role);
        return http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity),
            new AuthenticationProperties { ExpiresUtc = login.ExpiresAt, IsPersistent = false, AllowRefresh = false });
    }

    /// <summary>Only ever go back to a page inside the console, never to another site.</summary>
    public static string SafeReturnUrl(string? returnUrl) =>
        !string.IsNullOrEmpty(returnUrl) && returnUrl.StartsWith('/') && !returnUrl.StartsWith("//") && !returnUrl.StartsWith("/\\") && !returnUrl.StartsWith("/login")
            ? returnUrl : "/";

    /// <summary>A random temporary password that satisfies the API's rules (10+ characters, a letter and a digit).</summary>
    public static string GeneratePassword()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";
        while (true)
        {
            var candidate = new string(RandomNumberGenerator.GetItems<char>(alphabet, 14));
            if (candidate.Any(char.IsLetter) && candidate.Any(char.IsDigit)) return candidate;
        }
    }
}
