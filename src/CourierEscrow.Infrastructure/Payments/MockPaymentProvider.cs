using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CourierEscrow.Application;
using CourierEscrow.Domain;
using Microsoft.Extensions.Options;

namespace CourierEscrow.Infrastructure.Payments;

public sealed class MockProviderOptions
{
    /// <summary>Shared secret used to sign webhooks (HMAC-SHA256, hex). Development only.</summary>
    public string WebhookSecret { get; set; } = "dev-only-secret";
}

/// <summary>
/// Stand-in provider for local development and demos. It accepts every payout and never calls anyone; the dev
/// endpoints (or you, with curl) play the provider's webhooks. Webhook body:
/// <c>{"eventId","kind":"collection|payout","reference","status":"succeeded|failed|reversed","amount","fee","currency"}</c>.
/// </summary>
public sealed class MockPaymentProvider(IOptions<MockProviderOptions> options, TimeProvider clock) : IPaymentProvider
{
    public string Name => "mock";
    public string SignatureHeader => "X-Signature";
    public string EventHeader => "X-Event";

    public Task<CollectionRequested> RequestCollectionAsync(CollectionRequest request, CancellationToken ct) =>
        Task.FromResult(new CollectionRequested($"MOCK-{request.Reference}", null, clock.GetUtcNow().AddMinutes(15)));

    public Task<CollectionStatusResult> GetCollectionStatusAsync(string reference, CancellationToken ct) =>
        Task.FromResult(new CollectionStatusResult(RemoteStatus.Unknown, null, 0));

    public Task<PayoutSubmission> SubmitPayoutAsync(PayoutInstruction i, CancellationToken ct) =>
        Task.FromResult(new PayoutSubmission(PayoutSubmissionOutcome.Accepted, $"MOCK-TX-{i.Reference}", null));

    public Task<PayoutStatusResult> GetPayoutStatusAsync(string reference, string? providerRef, DateOnly submittedOn, CancellationToken ct) =>
        Task.FromResult(new PayoutStatusResult(RemoteStatus.Unknown, null, 0, null));

    public Task<IReadOnlyList<ProviderPayoutRecord>> ListPayoutsAsync(DateOnly day, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ProviderPayoutRecord>>([]);

    public Task<long?> GetBalanceAsync(string currency, CancellationToken ct) => Task.FromResult<long?>(null);

    public bool VerifyWebhook(byte[] rawBody, string? signature)
    {
        if (string.IsNullOrWhiteSpace(signature)) return false;
        var expected = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(options.Value.WebhookSecret), rawBody)).ToLowerInvariant();
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(signature.Trim().ToLowerInvariant()));
    }

    public ProviderNotification? ParseWebhook(string? eventCode, string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            string? Str(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            long Num(string name) => root.TryGetProperty(name, out var v) && v.TryGetInt64(out var n) ? n : 0;

            var id = Str("eventId");
            var reference = Str("reference");
            var status = Str("status")?.ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(reference) || status is null) return null;

            if (string.Equals(Str("kind"), "payout", StringComparison.OrdinalIgnoreCase))
            {
                var kind = status switch { "succeeded" => PayoutEventKind.Succeeded, "failed" => PayoutEventKind.Failed, "reversed" => PayoutEventKind.Reversed, _ => (PayoutEventKind?)null };
                return kind is null ? null : new PayoutNotification(id, reference, kind.Value, Num("amount"), Num("fee"), Str("reason"), Str("providerRef"));
            }

            var ck = status switch { "succeeded" => CollectionEventKind.Succeeded, "failed" => CollectionEventKind.Failed, "reversed" => CollectionEventKind.Reversed, _ => (CollectionEventKind?)null };
            return ck is null ? null : new CollectionNotification(id, reference, ck.Value, Num("amount"), Num("fee"), Str("currency") ?? "MWK", Str("providerRef"));
        }
        catch (JsonException) { return null; }
    }

    /// <summary>Signs a body the way the mock expects. Used by the dev endpoint and the demo script.</summary>
    public string Sign(string body) =>
        Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(options.Value.WebhookSecret), Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
}
