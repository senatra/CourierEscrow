using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CourierEscrow.Application.Helpers;

public static class Roles
{
    public const string OpsAgent = "ops_agent";
    public const string OpsAdmin = "ops_admin";
    public const string Finance = "finance";
    public const string Courier = "courier";
    public const string System = "system";

    /// <summary>The roles a staff user can hold. Couriers and the system actor are not staff accounts.</summary>
    public static readonly IReadOnlyList<string> Staff = [OpsAgent, OpsAdmin, Finance];

    public static bool IsStaff(string? role) => role is OpsAgent or OpsAdmin or Finance;
}

public sealed class AuthOptions
{
    /// <summary>Signs courier access tokens. At least 32 characters; set it from a secret store.</summary>
    public string TokenSecret { get; set; } = "dev-only-token-secret-change-me-0123456789";
    public int TokenLifetimeHours { get; set; } = 12;

    /// <summary>Staff sessions are shorter than courier ones: a console left open all night should not stay signed in.</summary>
    public int StaffTokenLifetimeHours { get; set; } = 8;

    /// <summary>The first administrator, created on startup when there are no staff users yet.</summary>
    public BootstrapAdminOptions BootstrapAdmin { get; set; } = new();

    /// <summary>Optional machine keys for scripts and CI. People sign in with a username and password instead.</summary>
    public List<StaffKey> StaffKeys { get; set; } = [];
}

public sealed class BootstrapAdminOptions
{
    public string Username { get; set; } = "admin";
    public string DisplayName { get; set; } = "Administrator";

    /// <summary>When empty, a random one-time password is generated and written to the log once. Either way it must be changed at first sign-in.</summary>
    public string? Password { get; set; }
}

/// <summary>A machine API key for scripts and CI. Keys come from configuration or a secret store, never the repo.</summary>
public sealed class StaffKey
{
    public string Name { get; set; } = "";
    public string Role { get; set; } = "";
    public string Key { get; set; } = "";
}

public sealed record TokenPrincipal(string Subject, string Role, string Name, DateTimeOffset ExpiresAt, string? Stamp = null);

/// <summary>Compact signed tokens: base64url(payload) + "." + base64url(HMAC-SHA256). Enough for the MVP courier web app.</summary>
public sealed class AccessTokens(AuthOptions options, TimeProvider clock)
{
    private readonly byte[] _key = Key(options.TokenSecret);

    private static byte[] Key(string secret)
    {
        if (string.IsNullOrWhiteSpace(secret) || secret.Length < 32)
            throw new InvalidOperationException("Auth:TokenSecret must be at least 32 characters.");
        return Encoding.UTF8.GetBytes(secret);
    }

    /// <param name="stamp">The user's security stamp. Staff tokens carry it so a password change, role change or disable revokes them.</param>
    /// <param name="lifetimeHours">Overrides the default lifetime.</param>
    public (string Token, DateTimeOffset ExpiresAt) Issue(string subject, string role, string name, string? stamp = null, int? lifetimeHours = null)
    {
        var expires = clock.GetUtcNow().AddHours(lifetimeHours ?? options.TokenLifetimeHours);
        var payload = JsonSerializer.SerializeToUtf8Bytes(new { sub = subject, role, name, stamp, exp = expires.ToUnixTimeSeconds() });
        var body = B64(payload);
        return ($"{body}.{B64(HMACSHA256.HashData(_key, Encoding.ASCII.GetBytes(body)))}", expires);
    }

    public TokenPrincipal? Validate(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        var parts = token.Split('.');
        if (parts.Length != 2) return null;
        try
        {
            var expected = HMACSHA256.HashData(_key, Encoding.ASCII.GetBytes(parts[0]));
            if (!CryptographicOperations.FixedTimeEquals(expected, FromB64(parts[1]))) return null;

            using var doc = JsonDocument.Parse(FromB64(parts[0]));
            var exp = DateTimeOffset.FromUnixTimeSeconds(doc.RootElement.GetProperty("exp").GetInt64());
            if (exp <= clock.GetUtcNow()) return null;
            var stamp = doc.RootElement.TryGetProperty("stamp", out var st) && st.ValueKind == JsonValueKind.String ? st.GetString() : null;
            return new TokenPrincipal(doc.RootElement.GetProperty("sub").GetString()!, doc.RootElement.GetProperty("role").GetString()!,
                doc.RootElement.GetProperty("name").GetString() ?? "", exp, stamp);
        }
        catch (Exception ex) when (ex is FormatException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    private static string B64(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromB64(string s)
    {
        var t = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(t.PadRight(t.Length + (4 - t.Length % 4) % 4, '='));
    }
}

/// <summary>PBKDF2 hashing for courier PINs: "v1.iterations.salt.hash".</summary>
public static class PinHasher
{
    private const int Iterations = 120_000;

    public static string Hash(string pin)
    {
        Validate(pin);
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(pin, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return $"v1.{Iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string pin, string stored)
    {
        var p = stored.Split('.');
        if (p.Length != 4 || p[0] != "v1" || !int.TryParse(p[1], out var iterations)) return false;
        var expected = Convert.FromBase64String(p[3]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(pin ?? "", Convert.FromBase64String(p[2]), iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    public static void Validate(string? pin)
    {
        if (pin is null || pin.Length is < 4 or > 8 || !pin.All(char.IsDigit))
            throw new Domain.DomainValidationException("PIN must be 4 to 8 digits.");
    }
}

/// <summary>
/// PBKDF2 hashing for staff passwords: "v1.iterations.salt.hash". The iteration count is stored with each hash,
/// so it can be raised later without locking anyone out.
/// </summary>
public static class PasswordHasher
{
    private const int Iterations = 210_000;
    public const int MinLength = 5;
    public const int MaxLength = 128;

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return $"v1.{Iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string? password, string stored)
    {
        var p = stored.Split('.');
        if (p.Length != 4 || p[0] != "v1" || !int.TryParse(p[1], out var iterations)) return false;
        var expected = Convert.FromBase64String(p[3]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(password ?? "", Convert.FromBase64String(p[2]), iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>Letters, digits, anything: only the length is checked. <paramref name="username"/> is kept so callers need not change.</summary>
    public static void Validate(string? password, string? username = null)
    {
        if (password is null || password.Length < MinLength)
            throw new Domain.DomainValidationException($"Password must be at least {MinLength} characters.");
        if (password.Length > MaxLength)
            throw new Domain.DomainValidationException($"Password must be at most {MaxLength} characters.");
    }

    /// <summary>A random password that passes <see cref="Validate"/>, without look-alike characters. Shown once, never stored.</summary>
    public static string Generate(string? username = null)
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";
        while (true)
        {
            var candidate = new string(RandomNumberGenerator.GetItems<char>(alphabet, 18));
            try { Validate(candidate, username); return candidate; }
            catch (Domain.DomainValidationException) { /* no digit or no letter this time; draw again */ }
        }
    }
}
