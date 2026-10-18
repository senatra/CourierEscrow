using System.Security.Cryptography;
using System.Text;
using CourierEscrow.Application.Interfaces;

namespace CourierEscrow.Infrastructure.Storage;

public sealed class StorageOptions
{
    /// <summary>Folder for photos in development. Swap <see cref="IObjectStorage"/> for S3 or Azure Blob in production.</summary>
    public string RootPath { get; set; } = "data/objects";

    /// <summary>Public base URL of this API, used to build upload links.</summary>
    public string PublicBaseUrl { get; set; } = "http://localhost:5080";
    public string SigningSecret { get; set; } = "dev-only-upload-signing-secret";
    public int UploadLinkMinutes { get; set; } = 10;
}

/// <summary>Disk storage with HMAC-signed, expiring upload links (the local stand-in for pre-signed S3 URLs).</summary>
public sealed class LocalObjectStorage(StorageOptions options) : IObjectStorage
{
    public UploadTarget CreateUploadTarget(Guid uploadId, DateTimeOffset now)
    {
        var expires = now.AddMinutes(options.UploadLinkMinutes);
        var unix = expires.ToUnixTimeSeconds();
        return new UploadTarget($"{options.PublicBaseUrl.TrimEnd('/')}/uploads/{uploadId}?exp={unix}&sig={Sign(uploadId, unix)}", expires);
    }

    public bool ValidateUploadTarget(Guid uploadId, long expiresUnix, string? signature, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(signature) || expiresUnix < now.ToUnixTimeSeconds()) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Sign(uploadId, expiresUnix)), Encoding.ASCII.GetBytes(signature));
    }

    public async Task SaveAsync(string objectKey, Stream content, CancellationToken ct)
    {
        var path = PathFor(objectKey);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var file = File.Create(path);
        await content.CopyToAsync(file, ct);
    }

    public Task<Stream?> OpenAsync(string objectKey, CancellationToken ct)
    {
        var path = PathFor(objectKey);
        return Task.FromResult<Stream?>(File.Exists(path) ? File.OpenRead(path) : null);
    }

    private string Sign(Guid uploadId, long unix) =>
        Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(options.SigningSecret), Encoding.UTF8.GetBytes($"{uploadId:N}.{unix}"))).ToLowerInvariant();

    private string PathFor(string objectKey)
    {
        var root = Path.GetFullPath(options.RootPath);
        var full = Path.GetFullPath(Path.Combine(root, objectKey));
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)) throw new InvalidOperationException("Invalid object key.");
        return full;
    }
}
