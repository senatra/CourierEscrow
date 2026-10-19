using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CourierEscrow.Application.Helpers;
using CourierEscrow.Application.Interfaces;
using CourierEscrow.Application.Services.Support;
using CourierEscrow.Domain.Models;

namespace CourierEscrow.Infrastructure.OneKhusa;

public sealed class OneKhusaWebhookSecretsOptions
{
    public string PayoutSuccess { get; set; } = "";
    public string PayoutFailed { get; set; } = "";
    public string PayoutReversed { get; set; } = "";
    public string PayRequestSuccess { get; set; } = "";
    public string PayRequestReversed { get; set; } = "";

    public static IReadOnlyList<string> RequiredEvents { get; } =
        ["payout.success", "payout.failed", "payout.reversed", "payrequest.success", "payrequest.reversed"];

    public string? GetSecret(string? eventCode) => eventCode?.Trim().ToLowerInvariant() switch
    {
        "payout.success" => PayoutSuccess,
        "payout.failed" => PayoutFailed,
        "payout.reversed" => PayoutReversed,
        "payrequest.success" => PayRequestSuccess,
        "payrequest.reversed" => PayRequestReversed,
        _ => null
    };

    public IReadOnlyList<string> MissingEvents => RequiredEvents.Where(code => string.IsNullOrWhiteSpace(GetSecret(code))).ToArray();
}

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

    public OneKhusaWebhookSecretsOptions WebhookSecrets { get; set; } = new();
    public string WebhookCallbackUrl { get; set; } = "";
    public int SandboxConnectorId { get; set; }
    public string CurrencyCode { get; set; } = "MWK";
    public string SandboxTestBeneficiaryAccountNumber { get; set; } = "111100000112";

    /// <summary>Connector ids for the mobile money networks, read from GET connectors (see /admin/provider/connectors).</summary>
    public int AirtelConnectorId { get; set; }
    public int TnmConnectorId { get; set; }

    public string ApiRoot => $"{BaseUrl.TrimEnd('/')}/{(Environment.Equals("Live", StringComparison.OrdinalIgnoreCase) ? "live" : "sandbox")}/v1/";

    public IReadOnlyList<string> MissingWebhookSecrets => WebhookSecrets.MissingEvents;

    public string? GetWebhookSecret(string? eventCode) => WebhookSecrets.GetSecret(eventCode);

    public void Validate()
    {
        if (MissingConfiguration().Count > 0)
            throw new InvalidOperationException("OneKhusa is selected but ApiKey, ApiSecret, OrganisationId, MerchantAccountNumber and CapturedBy are not all set.");
    }

    public IReadOnlyList<string> MissingConfiguration()
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(ApiKey)) missing.Add("ApiKey");
        if (string.IsNullOrWhiteSpace(ApiSecret)) missing.Add("ApiSecret");
        if (string.IsNullOrWhiteSpace(OrganisationId)) missing.Add("OrganisationId");
        if (MerchantAccountNumber <= 0) missing.Add("MerchantAccountNumber");
        if (string.IsNullOrWhiteSpace(CapturedBy)) missing.Add("CapturedBy");
        return missing;
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
public sealed record OneKhusaFakeCollectionResult(string TransactionReference, long Fee);

/// <summary>
/// OneKhusa adapter: OAuth token from the API key and secret, request-to-pay collections, single disbursements,
/// idempotency keys on every mutating call, and local HMAC-SHA512 webhook verification.
/// Collection status and the provider balance are not exposed by the documented API, so those report "unknown".
/// </summary>
public sealed class OneKhusaProvider(IHttpClientFactory factory, OneKhusaOptions options, OneKhusaTokenCache cache, TimeProvider clock) : IPaymentProvider
{
    public const string HttpClientName = "onekhusa";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string Name => "onekhusa";
    public bool SupportsPayoutInquiry => true;
    public string SignatureHeader => "X-OneKhusa-Webhook-Signature";
    public string EventHeader => "X-OneKhusa-Webhook-Event";

    public async Task<CollectionRequested> RequestCollectionAsync(CollectionRequest r, CancellationToken ct)
    {
        var requestBody = new
        {
            merchantAccountNumber = options.MerchantAccountNumber,
            referenceNumber = r.Reference,
            transactionAmount = Money.ToMajor(r.Amount),
            transactionDescription = r.Description,
            capturedBy = options.CapturedBy
        };
        var requestFingerprint = Fingerprint(requestBody);
        try
        {
            using var doc = await SendAsync(HttpMethod.Post, "collections/requestToPay/initiate", requestBody,
                r.IdempotencyKey ?? r.Reference, ct);

            var root = doc.RootElement;
            var tan = Str(root, "timedAccountNumber");
            DateTimeOffset? expires = root.TryGetProperty("expiryDate", out var e) && e.TryGetDateTimeOffset(out var dt)
                ? dt.ToUniversalTime()
                : null;
            if (tan is null) throw new ProviderException("OneKhusa did not return a timed account number.", transient: false);
            return new CollectionRequested(tan, tan, expires, requestFingerprint, 200);
        }
        catch (ProviderException ex)
        {
            throw new ProviderException(ex.Message, ex.Transient, ex, ex.StatusCode, requestFingerprint, ex.IdempotencyConflict);
        }
    }

    public async Task<OneKhusaFakeCollectionResult> SimulateAcceptRequestToPayAsync(string timedAccountNumber, long amount, CancellationToken ct)
    {
        EnsureSandbox();
        if (options.SandboxConnectorId <= 0) throw new InvalidOperationException("Set OneKhusa:SandboxConnectorId before simulating a sandbox collection.");
        using var doc = await SendAsync(HttpMethod.Post, "collections/requestToPay/addFakeTransaction", new
        {
            merchantAccountNumber = options.MerchantAccountNumber,
            transactionAmount = Money.ToMajor(amount),
            connectorId = options.SandboxConnectorId,
            timedAccountNumber,
            currencyCode = options.CurrencyCode,
            capturedBy = options.CapturedBy
        }, $"fake-collection-{timedAccountNumber}", ct);

        var tx = doc.RootElement.TryGetProperty("transaction", out var transaction) ? transaction : doc.RootElement;
        var code = Str(tx, "responseCode") ?? Str(doc.RootElement, "responseCode");
        var transactionStatusCode = Str(tx, "transactionStatusCode") ?? Str(doc.RootElement, "transactionStatusCode");
        var statusName = Str(tx, "transactionStatusName") ?? Str(doc.RootElement, "transactionStatusName");
        var isSuccessful = code?.StartsWith('S') == true
                           || string.Equals(transactionStatusCode, "S", StringComparison.OrdinalIgnoreCase)
                           || string.Equals(statusName, "Success", StringComparison.OrdinalIgnoreCase);
        if (!isSuccessful)
        {
            var responseMessage = Str(tx, "responseMessage") ?? Str(doc.RootElement, "responseMessage");
            var details = string.Join("; ", new[] { statusName, responseMessage }.Where(value => !string.IsNullOrWhiteSpace(value)));
            var suffix = string.IsNullOrWhiteSpace(details) ? "" : $" ({details})";
            throw new ProviderException($"OneKhusa sandbox collection simulation failed: {code ?? "no response code"}{suffix}", transient: false);
        }
        var transactionReference = Str(tx, "transactionReferenceNumber") ?? timedAccountNumber;
        var fee = tx.TryGetProperty("transactionFee", out var feeValue) && feeValue.TryGetDecimal(out var feeMajor) ? Money.ToMinor(feeMajor) : 0;
        return new OneKhusaFakeCollectionResult(transactionReference, fee);
    }

    public async Task TopUpSandboxMerchantAsync(long amount, CancellationToken ct)
    {
        EnsureSandbox();
        if (options.SandboxConnectorId <= 0) throw new InvalidOperationException("Set OneKhusa:SandboxConnectorId before topping up the sandbox merchant account.");
        if (amount <= 0) throw new ArgumentOutOfRangeException(nameof(amount), "Top-up amount must be positive.");
        using var doc = await SendAsync(HttpMethod.Post, "collections/accounts/topup", new
        {
            merchantAccountNumber = options.MerchantAccountNumber,
            connectorId = options.SandboxConnectorId,
            topupAmount = Money.ToMajor(amount),
            createdBy = options.CapturedBy
        }, $"sandbox-topup:{Guid.NewGuid():N}", ct);
        var tx = doc.RootElement.TryGetProperty("transaction", out var transaction) ? transaction : doc.RootElement;
        var code = Str(tx, "responseCode") ?? Str(doc.RootElement, "responseCode");
        if (code is null || !code.StartsWith('S')) throw new ProviderException($"OneKhusa sandbox top-up failed: {code ?? "no response code"}", transient: false);
    }

    private void EnsureSandbox()
    {
        if (!options.Environment.Equals("Sandbox", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("This fake-data action is only available when OneKhusa Environment is Sandbox.");
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
                beneficiaryAccountNumber = options.Environment.Equals("Sandbox", StringComparison.OrdinalIgnoreCase) ? options.SandboxTestBeneficiaryAccountNumber : i.Msisdn,
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
        var reason = row?.Status switch
        {
            RemoteStatus.Failed => "Transfer failed at OneKhusa.",
            RemoteStatus.Reversed => "Transfer was reversed by OneKhusa.",
            RemoteStatus.RequiresReview => $"OneKhusa status {row.StatusCode ?? "unknown"} requires manual review.",
            _ => null
        };
        return row is null ? new(RemoteStatus.Unknown, null, 0, null) : new(row.Status, row.Amount, row.Fee, reason);
    }

    public async Task<IReadOnlyList<ProviderPayoutRecord>> ListPayoutsAsync(DateOnly day, CancellationToken ct) =>
        await QueryPayoutsAsync(day, "", ct);

    public Task<long?> GetBalanceAsync(string currency, CancellationToken ct) => Task.FromResult<long?>(null);

    /// <summary>Lists the connectors so an admin can find the Airtel and TNM ids to put in configuration.</summary>
    public async Task<IReadOnlyList<OneKhusaConnector>> ListConnectorsAsync(CancellationToken ct)
    {
        using var doc = await SendAsync(HttpMethod.Get, "core/connectors/GetAll", null, null, ct);
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
                var status = (Str(r, "transactionStatusCode") ?? "").ToUpperInvariant() switch
                {
                    "S" => RemoteStatus.Succeeded,
                    "P" or "D" or "T" => RemoteStatus.Pending,
                    "F" => RemoteStatus.Failed,
                    "E" or "X" or "C" => RemoteStatus.RequiresReview,
                    "R" => RemoteStatus.Reversed,
                    _ => RemoteStatus.Unknown
                };
                var amount = r.TryGetProperty("transactionAmount", out var amt) && amt.TryGetDecimal(out var d) ? Money.ToMinor(d) : 0;
                var fee = r.TryGetProperty("transactionFee", out var feeValue) && feeValue.TryGetDecimal(out var feeMajor) ? Money.ToMinor(feeMajor) : 0;
                all.Add(new ProviderPayoutRecord(Str(r, "transactionReferenceNumber") ?? "", Str(r, "sourceReferenceNumber"),
                    amount, status, fee, Str(r, "currencyCode"), Str(r, "transactionStatusCode")));
            }
            if (rows.Count < 100) break;
        }
        return all;
    }

    public Task<bool> VerifyWebhookAsync(byte[] rawBody, string? eventCode, string? signature, CancellationToken ct)
    {
        var secret = options.GetWebhookSecret(eventCode);
        if (string.IsNullOrWhiteSpace(signature) || rawBody.Length == 0 || string.IsNullOrEmpty(secret)) return Task.FromResult(false);
        var expected = Convert.ToHexString(HMACSHA512.HashData(Encoding.UTF8.GetBytes(secret), rawBody)).ToLowerInvariant();
        return Task.FromResult(CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(signature.Trim().ToLowerInvariant())));
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
                    new { apiKey = options.ApiKey, apiSecret = options.ApiSecret, organisationId = options.OrganisationId, merchantAccountNumber = options.MerchantAccountNumber }, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                throw new ProviderException($"OneKhusa could not be reached: {ex.Message}", transient: true, ex);
            }
            using var _ = res;
            var text = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode)
                throw new ProviderException($"OneKhusa sign-in failed ({(int)res.StatusCode}): {Message(text)}",
                    transient: (int)res.StatusCode >= 500, statusCode: (int)res.StatusCode);

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
                var message = Message(text);
                var idempotencyConflict = res.StatusCode == HttpStatusCode.Conflict
                    && message.Contains("idempotency", StringComparison.OrdinalIgnoreCase)
                    && message.Contains("different", StringComparison.OrdinalIgnoreCase);
                throw new ProviderException($"OneKhusa returned {(int)res.StatusCode}: {message}", transient,
                    statusCode: (int)res.StatusCode, idempotencyConflict: idempotencyConflict);
            }
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.String or JsonValueKind.Number ? v.ToString() : null;

    private static string Fingerprint(object body) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(body, Json)));

    private static string Message(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            var summary = Str(root, "detail") ?? Str(root, "message") ?? Str(root, "title");
            if (root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Object)
            {
                var details = errors.EnumerateObject().Select(error =>
                {
                    var messages = error.Value.ValueKind == JsonValueKind.Array
                        ? string.Join(", ", error.Value.EnumerateArray().Select(value => value.ToString()))
                        : error.Value.ToString();
                    return $"{error.Name}: {messages}";
                });
                var validationDetails = string.Join("; ", details);
                if (!string.IsNullOrWhiteSpace(validationDetails))
                    return string.IsNullOrWhiteSpace(summary) ? validationDetails : $"{summary} {validationDetails}";
            }
            return summary ?? text;
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
                var reference = TryGetProperty(root, "metaData", out var meta) ? Text(meta, "referenceNumber") : null;
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
        TryGetProperty(e, name, out var v) && v.ValueKind is JsonValueKind.String or JsonValueKind.Number ? v.ToString() : null;

    private static long Minor(JsonElement e, string name) =>
        TryGetProperty(e, name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d) ? Money.ToMinor(d) : 0;

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }
}
