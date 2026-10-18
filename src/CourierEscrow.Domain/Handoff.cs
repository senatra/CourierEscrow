using CourierEscrow.Domain.Models;
using System.Security.Cryptography;
using System.Text;

namespace CourierEscrow.Domain;

public enum OtpStatus { Active = 0, Used = 1, Locked = 2 }
public enum OtpResult { Accepted, Wrong, Expired, Locked, NotActive }
public sealed record OtpCheck(OtpResult Result, int AttemptsLeft);

public static class OtpHasher
{
    /// <summary>Six digits from a cryptographic random source.</summary>
    public static string NewCode() => RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");

    /// <summary>Keyed per order: HMAC(server secret, order id) is the key for HMAC(code). The code itself is never stored.</summary>
    public static string Compute(string serverSecret, Guid escrowId, string code)
    {
        var orderKey = HMACSHA256.HashData(Encoding.UTF8.GetBytes(serverSecret), escrowId.ToByteArray());
        return Convert.ToHexString(HMACSHA256.HashData(orderKey, Encoding.UTF8.GetBytes(code.Trim()))).ToLowerInvariant();
    }
}

/// <summary>The buyer's one-time code, stored only as a keyed hash, limited by expiry and attempts, tied to one order.</summary>
public sealed class Otp
{
    public const int MaxAttempts = 5;

    private Otp() { Hmac = ""; }

    public Guid EscrowId { get; private set; }
    public string Hmac { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public int Attempts { get; private set; }
    public OtpStatus Status { get; private set; }

    public static Otp Issue(Guid escrowId, string serverSecret, string code, DateTimeOffset expiresAt) =>
        new() { EscrowId = escrowId, Hmac = OtpHasher.Compute(serverSecret, escrowId, code), ExpiresAt = expiresAt, Status = OtpStatus.Active };

    /// <summary>Ops issued a fresh code: new hash, new expiry, attempts reset, active again.</summary>
    public void Reissue(string serverSecret, string code, DateTimeOffset expiresAt)
    {
        Hmac = OtpHasher.Compute(serverSecret, EscrowId, code);
        ExpiresAt = expiresAt;
        Attempts = 0;
        Status = OtpStatus.Active;
    }

    public OtpCheck Verify(string serverSecret, string? code, DateTimeOffset now)
    {
        if (Status == OtpStatus.Locked) return new(OtpResult.Locked, 0);
        if (Status == OtpStatus.Used) return new(OtpResult.NotActive, 0);
        if (now > ExpiresAt) return new(OtpResult.Expired, MaxAttempts - Attempts);

        var candidate = OtpHasher.Compute(serverSecret, EscrowId, code ?? "");
        if (CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(candidate), Encoding.ASCII.GetBytes(Hmac)))
        {
            Status = OtpStatus.Used;
            return new(OtpResult.Accepted, MaxAttempts - Attempts);
        }

        Attempts++;
        if (Attempts >= MaxAttempts)
        {
            Status = OtpStatus.Locked;
            return new(OtpResult.Locked, 0);
        }
        return new(OtpResult.Wrong, MaxAttempts - Attempts);
    }
}

public enum ProofKind { Pickup = 0, Arrival = 1, Rejection = 2, Return = 3, Listing = 4 }

/// <summary>A photo slot. The courier or ops asks for an upload URL, the server stores the bytes and records the SHA-256 itself.</summary>
public sealed class Upload
{
    private Upload() { ObjectKey = ""; ContentType = ""; }

    public Guid Id { get; private set; }
    public Guid? CourierId { get; private set; }
    public string ObjectKey { get; private set; }
    public string ContentType { get; private set; }
    public string? Sha256 { get; private set; }
    public long SizeBytes { get; private set; }
    public bool Stored { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? StoredAt { get; private set; }

    public static Upload Create(Guid? courierId, string contentType, DateTimeOffset now)
    {
        var id = Guid.NewGuid();
        var folder = courierId is null ? "listings" : "proofs";
        return new Upload { Id = id, CourierId = courierId, ContentType = contentType, ObjectKey = $"{folder}/{now:yyyy/MM}/{id:N}", CreatedAt = now };
    }

    public void MarkStored(string sha256, long sizeBytes, DateTimeOffset now)
    {
        if (Stored) throw new DomainValidationException("This upload slot was already used.");
        Sha256 = sha256; SizeBytes = sizeBytes; Stored = true; StoredAt = now;
    }
}

/// <summary>Evidence attached to an order. The hash makes later tampering detectable.</summary>
public sealed class Proof
{
    private Proof() { ObjectKey = ""; Sha256 = ""; }

    public Guid Id { get; private set; }
    public Guid EscrowId { get; private set; }
    public ProofKind Kind { get; private set; }
    public Guid UploadId { get; private set; }
    public string ObjectKey { get; private set; }
    public string Sha256 { get; private set; }
    public double? Latitude { get; private set; }
    public double? Longitude { get; private set; }
    public DateTimeOffset? ClientTime { get; private set; }
    public DateTimeOffset ServerTime { get; private set; }
    public Guid? CourierId { get; private set; }

    public static Proof Create(Escrow e, ProofKind kind, Upload upload, GeoPoint? gps, DateTimeOffset? clientTime, DateTimeOffset now)
    {
        if (!upload.Stored || upload.Sha256 is null) throw new DomainValidationException("A photo was not uploaded yet.");
        return new Proof
        {
            Id = Guid.NewGuid(), EscrowId = e.Id, Kind = kind, UploadId = upload.Id, ObjectKey = upload.ObjectKey, Sha256 = upload.Sha256,
            Latitude = gps?.Latitude, Longitude = gps?.Longitude, ClientTime = clientTime?.ToUniversalTime(), ServerTime = now, CourierId = upload.CourierId
        };
    }
}
