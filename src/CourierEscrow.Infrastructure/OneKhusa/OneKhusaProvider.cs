using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CourierEscrow.Application;
using CourierEscrow.Domain;

namespace CourierEscrow.Infrastructure.OneKhusa;

public sealed class OneKhusaOptions
{
    public string BaseUrl { get; set; } = "https://api.onekhusa.com";

    /// <summary>"Sandbox" or "Live". Chooses the /sandbox/v1 or /live/v1 path.</summary>
    public string Environment { get; set; } = "Sandbox";
    public string ApiKey { get; set; } = "";
    public string ApiSecret { get; set; } = "";
    public string OrganisationId { get; set; } = "";
    public long MerchantAccountNumber { get; set; }

    /// <summary>Email of the staff member the provider records as the capturer.</summary>
    public string CapturedBy { get; set; } = "";

    /// <summary>HMAC-SHA512 secret for webhooks. Never in the repo.</summary>
    public string WebhookSecret { get; set; } = "";

    /// <summary>Connector ids for the mobile money networks, read from GET connectors (see /admin/provider/connectors).</summary>
    public int AirtelConnectorId { get; set; }
    public int TnmConnectorId { get; set; }

    public string ApiRoot => $"{BaseUrl.TrimEnd('/')}/{(Environment.Equals("Live", StringComparison.OrdinalIgnoreCase) ? "live" : "sandbox")}/v1/";

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ApiKey) || string.IsNullOrWhiteSpace(ApiSecret) || string.IsNullOrWhiteSpace(OrganisationId)
            || MerchantAccountNumber <= 0 || string.IsNullOrWhiteSpace(CapturedBy) || string.IsNullOrWhiteSpace(WebhookSecret))
            throw new InvalidOperationException("OneKhusa is selected but ApiKey, ApiSecret, OrganisationId, MerchantAccountNumber, CapturedBy and WebhookSecret are not all set.");
    }
}

/// <summary>Holds the short-lived (5 minute) access token so every call does not sign in again.</summary>
public sealed class OneKhusaTokenCache
{
    public SemaphoreSlim Gate { get; } = new(1, 1);
    public string? Token { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}

public sealed record OneKhusaConnector(int ConnectorId, string Name, string? ChannelCode, bool IsActive, MobileNetwork? GuessedNetwork);

/// <summary>
/// OneKhusa adapter: OAuth token from the API key and secret, request-to-pay collections, single disbursements,
/// idempotency keys on every mutating call, webhook verification (HMAC-SHA512 over the raw body).
/// Collection status and the provider balance are not exposed by the documented API, so those report "unknown".
/// </summary>
public sealed class OneKhusaProvider(IHttpClientFactory factory, OneKhusaOptions options, OneKhusaTokenCache cache, TimeProvider clock) : IPaymentProvider
{
    public const string HttpClientName = "onekhusa";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string Name => "onekhusa";
    public string SignatureHeader => "X-OneKhusa-Webhook-Signature";
    public string EventHeader => "X-OneKhusa-Webhook-Event";

    public async Task<CollectionRequested> RequestCollectionAsync(CollectionRequest r, CancellationToken ct)
    {
        using var doc = await SendAsync(HttpMethod.Post, "collections/requestToPay/initiate", new
        {
            merchantAccountNumber = options.MerchantAccountNumber,
            referenceNumber = r.Reference,
            transactionAmount = Money.ToMajor(r.Amount),
            transactionDescription = r.Description,
            capturedBy = options.CapturedBy
        }, r.Reference, ct);

        var root = doc.RootElement;
        var tan = Str(root, "timedAccountNumber");
        DateTimeOffset? expires = root.TryGetProperty("expiryDate", out var e) && e.TryGetDateTimeOffset(out var dt) ? dt : null;
        if (tan is null) throw new ProviderException("OneKhusa did not return a timed account number.", transient: false);
        return new CollectionRequested(tan, tan, expires);
    }

    public Task<CollectionStatusResult> GetCollectionStatusAsync(string reference, CancellationToken ct) =>
        Task.FromResult(new CollectionStatusResult(RemoteStatus.Unknown, null, 0));

    public async Task<PayoutSubmission> SubmitPayoutAsync(PayoutInstruction i, CancellationToken ct)
    {
        MobileNetwork? net = null;
        if (!string.IsNullOrWhiteSpace(i.Network) && Enum.TryParse<MobileNetwork>(i.Network, true, out var parsed))
            net = parsed;

        var connector = net switch
        {
            MobileNetwork.Airtel => options.AirtelConnectorId,
            MobileNetwork.Tnm => options.TnmConnectorId,
            _ => 0
        };
        if (connector <= 0)
            return new PayoutSubmission(PayoutSubmissionOutcome.Rejected, null, $"No OneKhusa connector is configured for {i.Network?.ToString() ?? "this wallet"}.");

        try
        {
            using var doc = await SendAsync(HttpMethod.Post, "disbursements/single/add", new
            {
                merchantAccountNumber = options.MerchantAccountNumber,
                beneficiaryName = i.BeneficiaryName,
                connectorId = connector,
                beneficiaryAccountNumber = i.Msisdn,
                transactionDescription = i.Description,
                transactionAmount = Money.ToMajor(i.Amount),
                sourceReferenceNumber = i.Reference,
                capturedBy = options.CapturedBy
            }, i.Reference, ct, extraHeaders: true);

            var root = doc.RootElement;
            var code = Str(root, "responseCode");
            var txRef = Str(root, "transactionReferenceNumber");
            if (code is not null && !code.StartsWith('S')) return new(PayoutSubmissionOutcome.Rejected, null, $"Provider response code {code}");
            return new(PayoutSubmissionOutcome.Accepted, string.IsNullOrWhiteSpace(txRef) ? null : txRef, null);
        }
        catch (ProviderException ex) when (!ex.Transient)
        {
            return new(PayoutSubmissionOutcome.Rejected, null, ex.Message);
        }
        catch (ProviderException ex)
        {
            return new(PayoutSubmissionOutcome.Transient, null, ex.Message);
        }
    }

    public async Task<PayoutStatusResult> GetPayoutStatusAsync(string reference, string? providerRef, DateOnly submittedOn, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(providerRef)) return new(RemoteStatus.Unknown, null, 0, null);
        var rows = await QueryPayoutsAsync(submittedOn, providerRef, ct);
        var row = rows.FirstOrDefault(r => string.Equals(r.TransactionRef, providerRef, StringComparison.OrdinalIgnoreCase));
        return row is null ? new(RemoteStatus.Unknown, null, 0, null) : new(row.Status, row.Amount, 0, row.Status == RemoteStatus.Failed ? "Failed at provider" : null);
    }

    public async Task<IReadOnlyList<ProviderPayoutRecord>> ListPayoutsAsync(DateOnly day, CancellationToken ct) =>
        await QueryPayoutsAsync(day, "", ct);

    public Task<long?> GetBalanceAsync(string currency, CancellationToken ct) => Task.FromResult<long?>(null);

    /// <summary>Lists the connectors so an admin can find the Airtel and TNM ids to put in configuration.</summary>
    public async Task<IReadOnlyList<OneKhusaConnector>> ListConnectorsAsync(CancellationToken ct)
    {
        using var doc = await SendAsync(HttpMethod.Get, "connectors/GetAll", null, null, ct);
        var list = new List<OneKhusaConnector>();
        foreach (var c in doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray() : Enumerable.Empty<JsonElement>())
        {
            var name = Str(c, "connectorName") ?? "";
            MobileNetwork? guess = name.Contains("airtel", StringComparison.OrdinalIgnoreCase) ? MobileNetwork.Airtel
                : name.Contains("tnm", StringComparison.OrdinalIgnoreCase) || name.Contains("mpamba", StringComparison.OrdinalIgnoreCase) ? MobileNetwork.Tnm : null;
            list.Add(new OneKhusaConnector(c.TryGetProperty("connectorId", out var id) && id.TryGetInt32(out var n) ? n : 0, name, Str(c, "channelCode"),
                !c.TryGetProperty("isActive", out var a) || a.ValueKind != JsonValueKind.False, guess));
        }
        return list;
    }

    private async Task<IReadOnlyList<ProviderPayoutRecord>> QueryPayoutsAsync(DateOnly day, string search, CancellationToken ct)
    {
        var all = new List<ProviderPayoutRecord>();
        for (var page = 1; page <= 50; page++)
        {
            using var doc = await SendAsync(HttpMethod.Post, "disbursements/single/getTransactions", new
            {
                merchantAccountNumber = options.MerchantAccountNumber,
                transactionDate = day.ToString("yyyy-MM-dd"),
                pageNumber = page,
                numberOfReturnedRows = 100,
                isIncremental = false,
                searchBy = "TransactionReferenceNumber",
                searchText = search
            }, null, ct);

            var rows = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray().ToList() : [];
            foreach (var r in rows)
            {
                var status = (Str(r, "transactionStatusCode") ?? "").ToUpperInvariant() switch { "S" => RemoteStatus.Succeeded, "P" => RemoteStatus.Pending, _ => RemoteStatus.Failed };
                all.Add(new ProviderPayoutRecord(Str(r, "transactionReferenceNumber") ?? "", Str(r, "sourceReferenceNumber"),
                    r.TryGetProperty("transactionAmount", out var amt) && amt.TryGetDecimal(out var d) ? Money.ToMinor(d) : 0, status));
            }
            if (rows.Count < 100) break;
        }
        return all;
    }

    public bool VerifyWebhook(byte[] rawBody, string? signature)
    {
        if (string.IsNullOrWhiteSpace(signature) || rawBody.Length == 0 || string.IsNullOrEmpty(options.WebhookSecret)) return false;
        var expected = Convert.ToHexString(HMACSHA512.HashData(Encoding.UTF8.GetBytes(options.WebhookSecret), rawBody)).ToLowerInvariant();
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(signature.Trim().ToLowerInvariant()));
    }

    public ProviderNotification? ParseWebhook(string? eventCode, string body) => OneKhusaWebhookParser.Parse(eventCode, body);

    // ---- HTTP plumbing -

    private async Task<string> GetTokenAsync(bool forceRefresh, CancellationToken ct)
    {
        await cache.Gate.WaitAsync(ct);
        try
        {
            if (!forceRefresh && cache.Token is not null && cache.ExpiresAt - TimeSpan.FromSeconds(30) > clock.GetUtcNow()) return cache.Token;

            HttpResponseMessage res;
            try
            {
                using var http = factory.CreateClient(HttpClientName);
                res = await http.PostAsJsonAsync(options.ApiRoot + "account/getAccessToken",
                    new { apiKey = options.ApiKey, apiSecret = options.ApiSecret, organisationId = options.OrganisationId }, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                throw new ProviderException($"OneKhusa could not be reached: {ex.Message}", transient: true, ex);
            }
            using var _ = res;
            var text = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode)
                throw new ProviderException($"OneKhusa sign-in failed ({(int)res.StatusCode}): {Message(text)}", transient: (int)res.StatusCode >= 500);

            using var doc = JsonDocument.Parse(text);
            cache.Token = Str(doc.RootElement, "accessToken") ?? throw new ProviderException("OneKhusa returned no access token.", transient: false);
            var minutes = doc.RootElement.TryGetProperty("expiryInMinutes", out var m) && m.TryGetInt32(out var n) ? n : 5;
            cache.ExpiresAt = clock.GetUtcNow().AddMinutes(minutes);
            return cache.Token;
        }
        finally { cache.Gate.Release(); }
    }

    private async Task<JsonDocument> SendAsync(HttpMethod method, string path, object? body, string? idempotencyKey, CancellationToken ct, bool extraHeaders = false)
    {
        for (var attempt = 0; ; attempt++)
        {
            var token = await GetTokenAsync(forceRefresh: attempt > 0, ct);
            using var req = new HttpRequestMessage(method, options.ApiRoot + path);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (idempotencyKey is not null) req.Headers.Add("X-Idempotency-Key", idempotencyKey);
            if (extraHeaders)
            {
                req.Headers.Add("X-Organisation-Id", options.OrganisationId);
                req.Headers.Add("X-Merchant-Account-Number", options.MerchantAccountNumber.ToString());
            }
            if (body is not null) req.Content = new StringContent(JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json");

            HttpResponseMessage res;
            try
            {
                using var http = factory.CreateClient(HttpClientName);
                res = await http.SendAsync(req, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                throw new ProviderException($"OneKhusa could not be reached: {ex.Message}", transient: true, ex);
            }

            using (res)
            {
                var text = await res.Content.ReadAsStringAsync(ct);
                if (res.StatusCode == HttpStatusCode.Unauthorized && attempt == 0) continue; // token expired early: sign in again once
                if (res.IsSuccessStatusCode)
                {
                    try { return JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text); }
                    catch (JsonException) { return JsonDocument.Parse(JsonSerializer.Serialize(new { message = text })); }
                }

                var transient = (int)res.StatusCode >= 500 || res.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout;
                throw new ProviderException($"OneKhusa returned {(int)res.StatusCode}: {Message(text)}", transient);
            }
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.String or JsonValueKind.Number ? v.ToString() : null;

    private static string Message(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            return Str(doc.RootElement, "detail") ?? Str(doc.RootElement, "message") ?? Str(doc.RootElement, "title") ?? text;
        }
        catch (JsonException) { return string.IsNullOrWhiteSpace(text) ? "no details" : text[..Math.Min(text.Length, 200)]; }
    }
}

/// <summary>Turns OneKhusa webhook bodies into notifications. Amounts arrive in kwacha and become tambala.</summary>
public static class OneKhusaWebhookParser
{
    public static ProviderNotification? Parse(string? eventCode, string body)
    {
        if (string.IsNullOrWhiteSpace(eventCode)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            var tx = Text(root, "transactionReferenceNumber");
            if (string.IsNullOrWhiteSpace(tx)) return null;
            var key = $"{eventCode.ToLowerInvariant()}:{tx}";
            var amount = Minor(root, "transactionAmount");
            var fee = Minor(root, "transactionFee");

            if (eventCode.StartsWith("payrequest.", StringComparison.OrdinalIgnoreCase))
            {
                var kind = eventCode.ToLowerInvariant() switch { "payrequest.success" => CollectionEventKind.Succeeded, "payrequest.reversed" => CollectionEventKind.Reversed, _ => (CollectionEventKind?)null };
                var reference = root.TryGetProperty("metaData", out var meta) ? Text(meta, "referenceNumber") : null;
                reference ??= Text(root, "referenceNumber") ?? Text(root, "sourceReferenceNumber");
                if (kind is null || string.IsNullOrWhiteSpace(reference)) return null;
                return new CollectionNotification(key, reference, kind.Value, amount, fee, Text(root, "currencyCode") ?? "MWK", tx);
            }

            if (eventCode.StartsWith("payout.", StringComparison.OrdinalIgnoreCase))
            {
                var kind = eventCode.ToLowerInvariant() switch { "payout.success" => PayoutEventKind.Succeeded, "payout.failed" => PayoutEventKind.Failed, "payout.reversed" => PayoutEventKind.Reversed, _ => (PayoutEventKind?)null };
                var source = Text(root, "sourceReferenceNumber");
                if (kind is null || string.IsNullOrWhiteSpace(source)) return null;
                var code = Text(root, "responseCode");
                return new PayoutNotification(key, source, kind.Value, amount, fee, kind == PayoutEventKind.Succeeded ? null : $"Provider response {code ?? "unknown"}", tx);
            }
            return null;
        }
        catch (JsonException) { return null; }
    }

    private static string? Text(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.String or JsonValueKind.Number ? v.ToString() : null;

    private static long Minor(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d) ? Money.ToMinor(d) : 0;
}
