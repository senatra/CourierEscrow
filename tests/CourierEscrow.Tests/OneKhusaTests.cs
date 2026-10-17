using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CourierEscrow.Application;
using CourierEscrow.Domain;
using CourierEscrow.Infrastructure.OneKhusa;
using CourierEscrow.Tests.Fakes;

namespace CourierEscrow.Tests;

public class OneKhusaProviderTests
{
    private sealed record Sent(HttpMethod Method, string Url, string Body, Dictionary<string, string> Headers);

    /// <summary>Plays the OneKhusa API: routes by the end of the URL path and records every request.</summary>
    private sealed class FakeApi : HttpMessageHandler
    {
        public List<Sent> Requests { get; } = [];
        public Dictionary<string, Queue<(HttpStatusCode Status, string Body)>> Routes { get; } = new();
        public int TokenCounter { get; private set; }
        public bool Unreachable { get; set; }

        public FakeApi On(string pathEnd, HttpStatusCode status, string body)
        {
            if (!Routes.TryGetValue(pathEnd, out var q)) Routes[pathEnd] = q = new();
            q.Enqueue((status, body));
            return this;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (Unreachable) throw new HttpRequestException("connection refused");
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Requests.Add(new Sent(request.Method, request.RequestUri!.ToString(), body, request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value))));

            foreach (var (end, queue) in Routes)
            {
                if (!request.RequestUri!.AbsolutePath.EndsWith(end, StringComparison.OrdinalIgnoreCase) || queue.Count == 0) continue;
                if (end.EndsWith("getAccessToken")) TokenCounter++;
                var (status, text) = queue.Count > 1 ? queue.Dequeue() : queue.Peek();
                return new HttpResponseMessage(status) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") };
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private const string TokenOk = "{\"accessToken\":\"tok-1\",\"expiresOn\":\"2026-10-14T09:05:00Z\",\"expiryInMinutes\":5}";
    private const string RtpOk = "{\"merchantAccountNumber\":12345678,\"timedAccountNumber\":\"7788990011\",\"expiryDate\":\"2026-10-14T09:15:00Z\",\"expiryInMinutes\":15}";

    private static (OneKhusaProvider P, FakeApi Api, TestClock Clock) Build(string environment = "Sandbox")
    {
        var api = new FakeApi().On("getAccessToken", HttpStatusCode.OK, TokenOk);
        var clock = new TestClock(TestEnv.Start);
        var options = new OneKhusaOptions
        {
            Environment = environment, ApiKey = "api-key", ApiSecret = "api-secret", OrganisationId = "ORG-123", MerchantAccountNumber = 12345678,
            CapturedBy = "ops@example.com", WebhookSecret = "hook-secret", AirtelConnectorId = 769375, TnmConnectorId = 769380
        };
        return (new OneKhusaProvider(new Factory(api), options, new OneKhusaTokenCache(), clock), api, clock);
    }

    private static CollectionRequest Req(string reference = "ES1234567890AB01", long amount = 1_000_000) =>
        new(reference, amount, "MWK", "265888111222", MobileNetwork.Tnm.ToString(), "Order NX-1");

    private static PayoutInstruction Pay(MobileNetwork? network = MobileNetwork.Airtel) =>
        new("PO1234567890AB01", 850_000, "MWK", "Mphatso Phiri", "265991234567", network?.ToString(), "Sale proceeds");

    [Fact]
    public async Task A_collection_signs_in_once_then_posts_with_an_idempotency_key_and_kwacha_amounts()
    {
        var (p, api, _) = Build();
        api.On("requestToPay/initiate", HttpStatusCode.OK, RtpOk);

        var result = await p.RequestCollectionAsync(Req(), default);

        Assert.Equal("7788990011", result.AccountNumber);
        Assert.Equal(new DateTimeOffset(2026, 10, 14, 9, 15, 0, TimeSpan.Zero), result.ExpiresAt);
        var rtp = Assert.Single(api.Requests.Where(r => r.Url.EndsWith("collections/requestToPay/initiate")));
        Assert.True(rtp.Url.StartsWith("https://api.onekhusa.com/sandbox/v1/"));
        Assert.Equal("Bearer tok-1", rtp.Headers["Authorization"]);
        Assert.Equal("ES1234567890AB01", rtp.Headers["X-Idempotency-Key"]);
        using var json = JsonDocument.Parse(rtp.Body);
        Assert.Equal(12345678L, json.RootElement.GetProperty("merchantAccountNumber").GetInt64());
        Assert.Equal(10_000m, json.RootElement.GetProperty("transactionAmount").GetDecimal());   // tambala to kwacha
        Assert.Equal("ES1234567890AB01", json.RootElement.GetProperty("referenceNumber").GetString());
        Assert.Equal("ops@example.com", json.RootElement.GetProperty("capturedBy").GetString());
    }

    [Fact]
    public async Task The_sign_in_request_carries_the_configured_credentials()
    {
        var (p, api, _) = Build();
        api.On("requestToPay/initiate", HttpStatusCode.OK, RtpOk);
        await p.RequestCollectionAsync(Req(), default);

        using var json = JsonDocument.Parse(api.Requests.First(r => r.Url.EndsWith("account/getAccessToken")).Body);
        Assert.Equal("api-key", json.RootElement.GetProperty("apiKey").GetString());
        Assert.Equal("api-secret", json.RootElement.GetProperty("apiSecret").GetString());
        Assert.Equal("ORG-123", json.RootElement.GetProperty("organisationId").GetString());
    }

    [Fact]
    public async Task The_access_token_is_reused_until_it_is_about_to_expire()
    {
        var (p, api, clock) = Build();
        api.On("requestToPay/initiate", HttpStatusCode.OK, RtpOk);

        await p.RequestCollectionAsync(Req("ES1"), default);
        await p.RequestCollectionAsync(Req("ES2"), default);
        Assert.Equal(1, api.TokenCounter);

        clock.Advance(TimeSpan.FromMinutes(5));
        await p.RequestCollectionAsync(Req("ES3"), default);
        Assert.Equal(2, api.TokenCounter);
    }

    [Fact]
    public async Task A_token_rejected_with_401_is_refreshed_and_the_call_retried_once()
    {
        var (p, api, _) = Build();
        api.On("requestToPay/initiate", HttpStatusCode.Unauthorized, "{}");
        api.On("requestToPay/initiate", HttpStatusCode.OK, RtpOk);

        var result = await p.RequestCollectionAsync(Req(), default);

        Assert.Equal("7788990011", result.AccountNumber);
        Assert.Equal(2, api.TokenCounter);
    }

    [Fact]
    public async Task The_live_environment_uses_the_live_base_url()
    {
        var (p, api, _) = Build("Live");
        api.On("requestToPay/initiate", HttpStatusCode.OK, RtpOk);
        await p.RequestCollectionAsync(Req(), default);
        Assert.True(api.Requests.All(r => r.Url.StartsWith("https://api.onekhusa.com/live/v1/")));
    }

    [Fact]
    public async Task Refusals_are_final_and_server_errors_are_temporary()
    {
        var (p, api, _) = Build();
        api.On("requestToPay/initiate", HttpStatusCode.BadRequest, "{\"message\":\"Insufficient permissions\"}");
        var refused = await Assert.ThrowsAsync<ProviderException>(() => p.RequestCollectionAsync(Req(), default));
        Assert.False(refused.Transient);
        Assert.Contains("Insufficient permissions", refused.Message);

        api.Routes["requestToPay/initiate"].Clear();
        api.On("requestToPay/initiate", HttpStatusCode.ServiceUnavailable, "down");
        Assert.True((await Assert.ThrowsAsync<ProviderException>(() => p.RequestCollectionAsync(Req(), default))).Transient);
    }

    [Fact]
    public async Task An_unreachable_provider_is_a_temporary_failure()
    {
        var (p, api, _) = Build();
        api.Unreachable = true;
        Assert.True((await Assert.ThrowsAsync<ProviderException>(() => p.RequestCollectionAsync(Req(), default))).Transient);
    }

    [Fact]
    public async Task Bad_credentials_surface_the_providers_message()
    {
        var api = new FakeApi().On("getAccessToken", HttpStatusCode.Unauthorized, "{\"message\":\"Invalid API key\"}");
        var p = new OneKhusaProvider(new Factory(api), new OneKhusaOptions { MerchantAccountNumber = 1, ApiKey = "k", ApiSecret = "s", OrganisationId = "o" }, new OneKhusaTokenCache(), new TestClock(TestEnv.Start));
        var ex = await Assert.ThrowsAsync<ProviderException>(() => p.RequestCollectionAsync(Req(), default));
        Assert.Contains("Invalid API key", ex.Message);
    }

    [Fact]
    public async Task A_payout_goes_to_the_wallet_through_the_networks_connector_with_the_extra_headers()
    {
        var (p, api, _) = Build();
        api.On("disbursements/single/add", HttpStatusCode.OK, "{\"merchantAccountNumber\":12345678,\"transactionReferenceNumber\":\"GWTX99\",\"responseCode\":\"S100\"}");

        var result = await p.SubmitPayoutAsync(Pay(MobileNetwork.Airtel), default);

        Assert.Equal(PayoutSubmissionOutcome.Accepted, result.Outcome);
        Assert.Equal("GWTX99", result.ProviderRef);
        var sent = Assert.Single(api.Requests.Where(r => r.Url.EndsWith("disbursements/single/add")));
        Assert.Equal("PO1234567890AB01", sent.Headers["X-Idempotency-Key"]);
        Assert.Equal("ORG-123", sent.Headers["X-Organisation-Id"]);
        Assert.Equal("12345678", sent.Headers["X-Merchant-Account-Number"]);
        using var json = JsonDocument.Parse(sent.Body);
        Assert.Equal(769375, json.RootElement.GetProperty("connectorId").GetInt32());
        Assert.Equal("265991234567", json.RootElement.GetProperty("beneficiaryAccountNumber").GetString());
        Assert.Equal("PO1234567890AB01", json.RootElement.GetProperty("sourceReferenceNumber").GetString());
        Assert.Equal(8_500m, json.RootElement.GetProperty("transactionAmount").GetDecimal());
    }

    [Fact]
    public async Task A_tnm_payout_uses_the_tnm_connector()
    {
        var (p, api, _) = Build();
        api.On("disbursements/single/add", HttpStatusCode.OK, "{\"transactionReferenceNumber\":\"GWTX1\",\"responseCode\":\"S100\"}");
        await p.SubmitPayoutAsync(Pay(MobileNetwork.Tnm), default);
        using var json = JsonDocument.Parse(api.Requests.Single(r => r.Url.EndsWith("disbursements/single/add")).Body);
        Assert.Equal(769380, json.RootElement.GetProperty("connectorId").GetInt32());
    }

    [Fact]
    public async Task A_wallet_with_no_configured_connector_is_refused_without_calling_the_provider()
    {
        var (p, api, _) = Build();
        var result = await p.SubmitPayoutAsync(Pay(network: null), default);
        Assert.Equal(PayoutSubmissionOutcome.Rejected, result.Outcome);
        Assert.Equal(0, api.Requests.Count);
    }

    [Fact]
    public async Task Payout_errors_are_classified_as_temporary_or_final()
    {
        var (p, api, _) = Build();
        api.On("disbursements/single/add", HttpStatusCode.InternalServerError, "oops");
        Assert.Equal(PayoutSubmissionOutcome.Transient, (await p.SubmitPayoutAsync(Pay(), default)).Outcome);

        api.Routes["disbursements/single/add"].Clear();
        api.On("disbursements/single/add", HttpStatusCode.TooManyRequests, "slow down");
        Assert.Equal(PayoutSubmissionOutcome.Transient, (await p.SubmitPayoutAsync(Pay(), default)).Outcome);

        api.Routes["disbursements/single/add"].Clear();
        api.On("disbursements/single/add", HttpStatusCode.BadRequest, "{\"message\":\"Invalid account\"}");
        var rejected = await p.SubmitPayoutAsync(Pay(), default);
        Assert.Equal(PayoutSubmissionOutcome.Rejected, rejected.Outcome);
        Assert.Contains("Invalid account", rejected.Message);

        api.Unreachable = true;
        Assert.Equal(PayoutSubmissionOutcome.Transient, (await p.SubmitPayoutAsync(Pay(), default)).Outcome);
    }

    [Fact]
    public async Task A_200_with_an_error_response_code_is_a_rejection()
    {
        var (p, api, _) = Build();
        api.On("disbursements/single/add", HttpStatusCode.OK, "{\"responseCode\":\"E233\",\"transactionReferenceNumber\":\"\"}");
        var result = await p.SubmitPayoutAsync(Pay(), default);
        Assert.Equal(PayoutSubmissionOutcome.Rejected, result.Outcome);
        Assert.Contains("E233", result.Message);
    }

    [Fact]
    public async Task Connectors_are_listed_with_a_guess_at_the_network_so_ops_can_configure_them()
    {
        var (p, api, _) = Build();
        api.On("connectors/GetAll", HttpStatusCode.OK,
            "[{\"connectorId\":11,\"connectorName\":\"Airtel Money\",\"channelCode\":\"M\",\"isActive\":true}," +
            "{\"connectorId\":12,\"connectorName\":\"TNM Mpamba\",\"channelCode\":\"M\",\"isActive\":true}," +
            "{\"connectorId\":13,\"connectorName\":\"National Bank\",\"channelCode\":\"B\",\"isActive\":false}]");

        var list = await p.ListConnectorsAsync(default);

        Assert.Equal(3, list.Count);
        Assert.Equal(MobileNetwork.Airtel, list.Single(c => c.ConnectorId == 11).GuessedNetwork);
        Assert.Equal(MobileNetwork.Tnm, list.Single(c => c.ConnectorId == 12).GuessedNetwork);
        Assert.Null(list.Single(c => c.ConnectorId == 13).GuessedNetwork);
        Assert.False(list.Single(c => c.ConnectorId == 13).IsActive);
    }

    [Fact]
    public async Task The_providers_payout_list_is_read_in_tambala_with_statuses_for_reconciliation()
    {
        var (p, api, _) = Build();
        api.On("disbursements/single/getTransactions", HttpStatusCode.OK,
            "[{\"transactionReferenceNumber\":\"GW1\",\"transactionAmount\":8500.00,\"transactionStatusCode\":\"S\"}," +
            "{\"transactionReferenceNumber\":\"GW2\",\"transactionAmount\":10.50,\"transactionStatusCode\":\"P\"}," +
            "{\"transactionReferenceNumber\":\"GW3\",\"transactionAmount\":1,\"transactionStatusCode\":\"F\"}]");

        var rows = await p.ListPayoutsAsync(new DateOnly(2026, 10, 14), default);

        Assert.Equal(3, rows.Count);
        Assert.Equal(850_000, rows[0].Amount);
        Assert.Equal(RemoteStatus.Succeeded, rows[0].Status);
        Assert.Equal(1_050, rows[1].Amount);
        Assert.Equal(RemoteStatus.Pending, rows[1].Status);
        Assert.Equal(RemoteStatus.Failed, rows[2].Status);
        using var json = JsonDocument.Parse(api.Requests.Single(r => r.Url.EndsWith("getTransactions")).Body);
        Assert.Equal("2026-10-14", json.RootElement.GetProperty("transactionDate").GetString());
    }

    [Fact]
    public async Task Payout_status_looks_up_the_providers_transaction_reference()
    {
        var (p, api, _) = Build();
        api.On("disbursements/single/getTransactions", HttpStatusCode.OK, "[{\"transactionReferenceNumber\":\"GW1\",\"transactionAmount\":8500.00,\"transactionStatusCode\":\"S\"}]");
        Assert.Equal(RemoteStatus.Succeeded, (await p.GetPayoutStatusAsync("PO1", "GW1", new DateOnly(2026, 10, 14), default)).Status);
        Assert.Equal(RemoteStatus.Unknown, (await p.GetPayoutStatusAsync("PO1", null, new DateOnly(2026, 10, 14), default)).Status);
    }

    // webhooks

    private static string Sign(byte[] body, string secret = "hook-secret") =>
        Convert.ToHexString(HMACSHA512.HashData(Encoding.UTF8.GetBytes(secret), body)).ToLowerInvariant();

    [Fact]
    public void Webhook_signatures_are_hmac_sha512_over_the_raw_body()
    {
        var (p, _, _) = Build();
        var body = Encoding.UTF8.GetBytes("{\"transactionReferenceNumber\":\"TX1\"}");
        var tampered = Encoding.UTF8.GetBytes("{\"transactionReferenceNumber\":\"TX2\"}");

        Assert.True(p.VerifyWebhook(body, Sign(body)));
        Assert.True(p.VerifyWebhook(body, Sign(body).ToUpperInvariant()));
        Assert.False(p.VerifyWebhook(body, Sign(body, "other-secret")));
        Assert.False(p.VerifyWebhook(tampered, Sign(body)));
        Assert.False(p.VerifyWebhook(body, null));
        Assert.False(p.VerifyWebhook(body, "not-a-signature"));
        Assert.False(p.VerifyWebhook([], Sign(body)));
    }

    [Fact]
    public void A_collection_success_is_parsed_with_our_reference_and_amounts_in_tambala()
    {
        const string body = """
            {"transactionReferenceNumber":"TX123","transactionAmount":100.00,"transactionFee":2.50,
             "metaData":{"timedAccountNumber":"7788990011","referenceNumber":"ES1234567890AB01"}}
            """;

        var n = Assert.IsType<CollectionNotification>(OneKhusaWebhookParser.Parse("payrequest.success", body));

        Assert.Equal("payrequest.success:TX123", n.EventKey);
        Assert.Equal("ES1234567890AB01", n.Reference);
        Assert.Equal(CollectionEventKind.Succeeded, n.Kind);
        Assert.Equal(10_000, n.Amount);
        Assert.Equal(250, n.Fee);
        Assert.Equal("MWK", n.Currency);
    }

    [Fact]
    public void Reversals_and_payout_events_are_parsed_by_event_code()
    {
        const string collection = """{"transactionReferenceNumber":"TX9","transactionAmount":50,"metaData":{"referenceNumber":"ES1"}}""";
        Assert.Equal(CollectionEventKind.Reversed, Assert.IsType<CollectionNotification>(OneKhusaWebhookParser.Parse("payrequest.reversed", collection)).Kind);

        const string payout = """{"transactionReferenceNumber":"TX55","sourceReferenceNumber":"PO1","transactionAmount":85.00,"transactionFee":1.00,"responseCode":"S100"}""";
        var ok = Assert.IsType<PayoutNotification>(OneKhusaWebhookParser.Parse("payout.success", payout));
        Assert.Equal(PayoutEventKind.Succeeded, ok.Kind);
        Assert.Equal("PO1", ok.SourceReference);
        Assert.Equal(8_500, ok.Amount);
        Assert.Equal(100, ok.Fee);
        Assert.Null(ok.Reason);

        var failed = Assert.IsType<PayoutNotification>(OneKhusaWebhookParser.Parse("payout.failed", payout.Replace("S100", "E233")));
        Assert.Equal(PayoutEventKind.Failed, failed.Kind);
        Assert.Contains("E233", failed.Reason);
        Assert.Equal(PayoutEventKind.Reversed, Assert.IsType<PayoutNotification>(OneKhusaWebhookParser.Parse("payout.reversed", payout)).Kind);
    }

    [Fact]
    public void Events_we_do_not_act_on_or_cannot_match_are_ignored()
    {
        Assert.Null(OneKhusaWebhookParser.Parse("payrequest.success", """{"transactionReferenceNumber":"TX1","transactionAmount":1}"""));   // no reference of ours
        Assert.Null(OneKhusaWebhookParser.Parse("payrequest.success", """{"metaData":{"referenceNumber":"R"}}"""));                            // no transaction id
        Assert.Null(OneKhusaWebhookParser.Parse("payment.success", """{"transactionReferenceNumber":"TX1","metaData":{"referenceNumber":"R"}}"""));
        Assert.Null(OneKhusaWebhookParser.Parse("payout.unknown", """{"transactionReferenceNumber":"TX1","sourceReferenceNumber":"P"}"""));
        Assert.Null(OneKhusaWebhookParser.Parse(null, """{"transactionReferenceNumber":"TX1"}"""));
        Assert.Null(OneKhusaWebhookParser.Parse("payrequest.success", "not json"));
        Assert.Null(OneKhusaWebhookParser.Parse("payrequest.success", "[1,2]"));
    }

    [Fact]
    public void Missing_configuration_is_caught_at_startup()
    {
        Assert.Throws<InvalidOperationException>(() => new OneKhusaOptions().Validate());
        new OneKhusaOptions { ApiKey = "k", ApiSecret = "s", OrganisationId = "o", MerchantAccountNumber = 1, CapturedBy = "a@b.c", WebhookSecret = "w" }.Validate();
    }
}
