using System.Text.RegularExpressions;
using CourierEscrow.Application;
using CourierEscrow.Application.Services;
using CourierEscrow.Domain;
using CourierEscrow.Infrastructure.Payments;
using Microsoft.Extensions.Options;

namespace CourierEscrow.Tests.Fakes;

public sealed class TestClock(DateTimeOffset now) : TimeProvider
{
    private DateTimeOffset _now = now;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now += by;
}

public sealed class FakeProtector : IProtector
{
    public string Protect(string plain) => "enc:" + plain;
    public string Unprotect(string protectedValue) => protectedValue.StartsWith("enc:") ? protectedValue[4..] : throw new InvalidOperationException("not protected");
}

public sealed class FakeSms : ISmsSender
{
    public List<(string To, string Body)> Sent { get; } = [];
    public bool Fail { get; set; }

    public Task<string?> SendAsync(string toPhone, string body, CancellationToken ct)
    {
        if (Fail) throw new HttpRequestException("aggregator down");
        Sent.Add((toPhone, body));
        return Task.FromResult<string?>($"sms-{Sent.Count}");
    }
}

public sealed class FakeStorage : IObjectStorage
{
    public Dictionary<string, byte[]> Objects { get; } = [];
    public UploadTarget CreateUploadTarget(Guid uploadId, DateTimeOffset now) => new($"https://files.test/uploads/{uploadId}", now.AddMinutes(10));
    public bool ValidateUploadTarget(Guid uploadId, long expiresUnix, string? signature, DateTimeOffset now) => signature == "ok";

    public async Task SaveAsync(string objectKey, Stream content, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        await content.CopyToAsync(ms, ct);
        Objects[objectKey] = ms.ToArray();
    }

    public Task<Stream?> OpenAsync(string objectKey, CancellationToken ct) =>
        Task.FromResult<Stream?>(Objects.TryGetValue(objectKey, out var b) ? new MemoryStream(b) : null);
}

/// <summary>A scriptable payment provider. Webhook parsing and signatures reuse the mock provider's format.</summary>
public sealed class FakeProvider(TimeProvider clock) : IPaymentProvider
{
    private readonly MockPaymentProvider _mock = new(Options.Create(new MockProviderOptions()), clock);

    public string Name => "fake";
    public string SignatureHeader => "X-Signature";
    public string EventHeader => "X-Event";

    public List<CollectionRequest> Collections { get; } = [];
    public List<PayoutInstruction> PayoutCalls { get; } = [];
    public Queue<PayoutSubmission> PayoutResults { get; } = new();
    public bool CollectionFails { get; set; }
    public string? AccountNumber { get; set; } = "7788990011";
    public CollectionStatusResult CollectionStatus { get; set; } = new(RemoteStatus.Unknown, null, 0);
    public PayoutStatusResult PayoutStatus { get; set; } = new(RemoteStatus.Unknown, null, 0, null);
    public List<ProviderPayoutRecord> Remote { get; } = [];
    public long? Balance { get; set; }

    public Task<CollectionRequested> RequestCollectionAsync(CollectionRequest request, CancellationToken ct)
    {
        Collections.Add(request);
        if (CollectionFails) throw new ProviderException("provider unreachable");
        return Task.FromResult(new CollectionRequested(AccountNumber, AccountNumber, clock.GetUtcNow().AddMinutes(15)));
    }

    public Task<CollectionStatusResult> GetCollectionStatusAsync(string reference, CancellationToken ct) => Task.FromResult(CollectionStatus);

    public Task<PayoutSubmission> SubmitPayoutAsync(PayoutInstruction instruction, CancellationToken ct)
    {
        PayoutCalls.Add(instruction);
        return Task.FromResult(PayoutResults.Count > 0 ? PayoutResults.Dequeue() : new PayoutSubmission(PayoutSubmissionOutcome.Accepted, $"TX-{instruction.Reference}", null));
    }

    public Task<PayoutStatusResult> GetPayoutStatusAsync(string reference, string? providerRef, DateOnly submittedOn, CancellationToken ct) => Task.FromResult(PayoutStatus);
    public Task<IReadOnlyList<ProviderPayoutRecord>> ListPayoutsAsync(DateOnly day, CancellationToken ct) => Task.FromResult<IReadOnlyList<ProviderPayoutRecord>>(Remote.ToList());
    public Task<long?> GetBalanceAsync(string currency, CancellationToken ct) => Task.FromResult(Balance);
    public bool VerifyWebhook(byte[] rawBody, string? signature) => _mock.VerifyWebhook(rawBody, signature);
    public ProviderNotification? ParseWebhook(string? eventCode, string body) => _mock.ParseWebhook(eventCode, body);
    public string Sign(string body) => _mock.Sign(body);
}

/// <summary>Wires every service to in-memory ports and offers shortcuts for the common setups. Amounts are tambala.</summary>
public sealed class TestEnv
{
    public static readonly DateTimeOffset Start = new(2026, 10, 14, 9, 0, 0, TimeSpan.Zero);

    // K300,000 item + K8,000 delivery, 2% platform fee on the item = K6,000 (the worked example in the design).
    public const long Item = 30_000_000;
    public const long Delivery = 800_000;
    public const long Total = Item + Delivery;
    public const long Fee = 600_000;

    public MemoryDb Db { get; } = new();
    public MemoryRepos Repos { get; }
    public TestClock Clock { get; } = new(Start);
    public FakeProvider Provider { get; }
    public FakeSms Sms { get; } = new();
    public FakeProtector Protector { get; } = new();
    public FakeStorage Storage { get; } = new();
    public EscrowOptions Options { get; } = new() { PlatformFeeBps = 200, OtpSecret = "test-otp-secret" };
    public AccessTokens Tokens { get; }

    public OrderService Orders { get; }
    public CourierService Couriers { get; }
    public CollectionService Collections { get; }
    public BuyerApprovalService Approval { get; }
    public DevSimulationService Sim { get; }
    public PayoutService Payouts { get; }
    public HandoffService Handoff { get; }
    public DisputeService Disputes { get; }
    public WebhookService Webhooks { get; }
    public NotificationDispatcher Dispatcher { get; }
    public ReconciliationService Recon { get; }
    public AlertsService Alerts { get; }

    public CourierAccount Courier { get; private set; } = null!;

    public TestEnv()
    {
        Repos = new MemoryRepos(Db);
        Provider = new FakeProvider(Clock);
        Tokens = new AccessTokens(new AuthOptions(), Clock);

        var commit = new EscrowCommit(Repos, Repos, Db);
        var notifier = new Notifier(Repos, Protector, Clock);
        Payouts = new PayoutService(Repos, Repos, Repos, Repos, Repos, Provider, Repos, commit, Clock);
        Orders = new OrderService(Repos, Repos, Repos, Repos, Repos, Repos, Repos, Repos, Repos, Repos, Repos, Db, Repos, Payouts, commit, notifier, Clock, Options);
        Couriers = new CourierService(Repos, Repos, Repos, Repos, Db, Tokens, Clock);
        Collections = new CollectionService(Repos, Repos, Repos, Repos, Repos, Provider, commit, notifier, Clock, Options);
        Handoff = new HandoffService(Repos, Repos, Repos, Repos, Repos, Repos, Repos, Storage, Repos, Payouts, commit, notifier, Clock, Options);
        Approval = new BuyerApprovalService(Orders, Collections);
        Sim = new DevSimulationService(Repos, Repos, Repos, Repos, commit, notifier, Handoff, Payouts, Clock, Options);
        Disputes = new DisputeService(Repos, Repos, Repos, Repos, Payouts, Repos, commit, notifier, Clock);
        Webhooks = new WebhookService(Provider, Repos, Collections, Payouts, Db, Clock);
        Dispatcher = new NotificationDispatcher(Repos, Sms, Protector, Db, Clock, new NullSmsCapture());
        Recon = new ReconciliationService(Provider, Repos, Repos, Repos, Repos, Db, Clock, Options);
        Alerts = new AlertsService(Repos, Repos, Repos, Repos, Repos, Repos, Repos, Clock, Options);
    }

    public async Task<TestEnv> InitAsync()
    {
        Courier = await Couriers.CreateAsync("Mphatso Banda", "0999000111", MobileNetwork.Airtel.ToString(), "4821", "staff:test");
        return this;
    }

    public static async Task<TestEnv> NewAsync() => await new TestEnv().InitAsync();

    public async Task<Escrow> CreateOrderAsync(long item = Item, long delivery = Delivery)
    {
        var photos = await PhotosAsync(3);
        return await Orders.CreateAsync(new CreateOrderCommand("Buyer Banda", "0888111222", MobileNetwork.Tnm.ToString(), "Chikondi Phones", "0991234567", MobileNetwork.Airtel.ToString(),
            Courier.Id, "Samsung A15", item, delivery, "MWK", "Area 25 market", "Kawale, Lilongwe", photos), "staff:ops");
    }

    public async Task<Escrow> ConfirmProductAsync(Escrow e)
    {
        await Orders.ConfirmProductAsync(e.TrackingToken);
        return e;
    }

    public async Task<Escrow> PendingOrderAsync()
    {
        var e = await CreateOrderAsync();
        await ConfirmProductAsync(e);
        await Collections.StartAsync(e.Id, "staff:ops");
        return e;
    }

    public CollectionNotification Paid(Escrow e, string? tx = null, long? amount = null, long fee = 0)
    {
        tx ??= $"TX-{e.OrderNumber}";
        return new($"payrequest.success:{tx}", e.PaymentReference!, CollectionEventKind.Succeeded, amount ?? e.Total, fee, e.Currency, tx);
    }

    public async Task<Escrow> FundedOrderAsync()
    {
        var e = await PendingOrderAsync();
        var outcome = await Collections.HandleAsync(Paid(e), null);
        Assert.Equal(CallbackOutcome.Processed, outcome);
        return e;
    }

    public async Task<List<Guid>> PhotosAsync(int count)
    {
        var ids = new List<Guid>();
        for (var i = 0; i < count; i++)
        {
            var slot = await Handoff.CreateUploadSlotAsync(Courier.Id, "image/jpeg");
            await Handoff.StoreUploadAsync(slot.UploadId, 0, "ok", new MemoryStream([1, 2, 3, (byte)i, 9]));
            ids.Add(slot.UploadId);
        }
        return ids;
    }

    public async Task<HandoffResult> PickupAsync(Escrow e, string eventId = "ev-pickup", int photos = 3) =>
        await Handoff.PickupAsync(Courier.Id, e.Id, new PickupCommand(eventId, await PhotosAsync(photos), 0.6m, "SL-558201", new GeoPoint(-13.97, 33.78), Start));

    public async Task<Escrow> PickedUpOrderAsync()
    {
        var e = await FundedOrderAsync();
        await PickupAsync(e);
        return e;
    }

    public async Task<Escrow> DeliveredOrderAsync()
    {
        var e = await PickedUpOrderAsync();
        await Handoff.ArriveAsync(Courier.Id, e.Id, new ArrivalCommand("ev-arrive", await PhotosAsync(1), new GeoPoint(-13.98, 33.79), Start));
        return e;
    }

    /// <summary>The buyer's code as the SMS aggregator would have received it.</summary>
    public string LastOtp()
    {
        var n = Db.Notifications.Rows.Last(x => x.Sensitive);
        var m = Regex.Match(Protector.Unprotect(n.Body), @"(\d{3}) (\d{3})");
        return m.Groups[1].Value + m.Groups[2].Value;
    }

    public async Task<Escrow> ReleasedOrderAsync()
    {
        var e = await DeliveredOrderAsync();
        var r = await Handoff.SubmitOtpAsync(Courier.Id, e.Id, "ev-otp", LastOtp());
        Assert.Equal(OtpResult.Accepted, r.Result);
        return e;
    }

    public Task<Escrow> GetAsync(Escrow e) => Orders.GetAsync(e.Id);
    public long Held(Escrow e) => Db.Balance(CourierEscrow.Domain.Ledger.Accounts.Escrow(e.Id));

    public async Task ConfirmAllPayoutsAsync(Escrow e)
    {
        await Payouts.ExecuteDueAsync();
        foreach (var p in Db.Payouts.Rows.Where(p => p.EscrowId == e.Id && p.Status == PayoutStatus.Submitted).ToList())
            await Payouts.ApplyAsync(new PayoutNotification($"payout.success:{p.Reference}", p.Reference, PayoutEventKind.Succeeded, p.Amount, 100, null, p.ProviderRef), null);
    }
}
