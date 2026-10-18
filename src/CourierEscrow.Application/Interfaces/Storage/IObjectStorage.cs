namespace CourierEscrow.Application.Interfaces;

public sealed record UploadTarget(string Url, DateTimeOffset ExpiresAt);

public interface IObjectStorage
{
    UploadTarget CreateUploadTarget(Guid uploadId, DateTimeOffset now);
    bool ValidateUploadTarget(Guid uploadId, long expiresUnix, string? signature, DateTimeOffset now);
    Task SaveAsync(string objectKey, Stream content, CancellationToken ct);
    Task<Stream?> OpenAsync(string objectKey, CancellationToken ct);
}
