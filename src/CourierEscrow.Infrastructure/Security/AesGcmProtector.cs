using System.Security.Cryptography;
using System.Text;
using CourierEscrow.Application.Interfaces;

namespace CourierEscrow.Infrastructure.Security;

public sealed class SecurityOptions
{
    /// <summary>Base64 of 32 random bytes. Supplied by a secret store in production; a development value lives in appsettings.Development.json.</summary>
    public string DataKey { get; set; } = "";
}

/// <summary>AES-256-GCM. Output is base64(nonce | tag | ciphertext), so tampering is detected on decrypt.</summary>
public sealed class AesGcmProtector : IProtector
{
    private readonly byte[] _key;

    public AesGcmProtector(SecurityOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.DataKey))
            throw new InvalidOperationException("Security:DataKey is missing. Configure the Security__DataKey environment variable with a base64-encoded 32-byte key.");

        try { _key = Convert.FromBase64String(options.DataKey); }
        catch (FormatException) { throw new InvalidOperationException("Security:DataKey must be base64. Configure Security__DataKey with a base64-encoded 32-byte key."); }
        if (_key.Length != 32)
            throw new InvalidOperationException("Security:DataKey must decode to exactly 32 bytes. Configure Security__DataKey with a base64-encoded 32-byte key.");
    }

    public string Protect(string plain)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var data = Encoding.UTF8.GetBytes(plain);
        var cipher = new byte[data.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(_key, 16);
        aes.Encrypt(nonce, data, cipher, tag);
        return Convert.ToBase64String([.. nonce, .. tag, .. cipher]);
    }

    public string Unprotect(string protectedValue)
    {
        var all = Convert.FromBase64String(protectedValue);
        if (all.Length < 28) throw new CryptographicException("Protected value is too short.");
        var plain = new byte[all.Length - 28];
        using var aes = new AesGcm(_key, 16);
        aes.Decrypt(all.AsSpan(0, 12), all.AsSpan(28), all.AsSpan(12, 16), plain);
        return Encoding.UTF8.GetString(plain);
    }
}
