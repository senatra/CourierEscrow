using System.Security.Cryptography;
using System.Text;
using CourierEscrow.Application.Helpers;
using CourierEscrow.Application.Services;
using CourierEscrow.Application.Services.Support;
using CourierEscrow.Domain;
using CourierEscrow.Domain.Models;
using CourierEscrow.Domain.Ledger;
using CourierEscrow.Infrastructure.Security;
using CourierEscrow.Infrastructure.Storage;
using CourierEscrow.Tests.Fakes;

namespace CourierEscrow.Tests;

public class WebhookTests
{
    private static byte[] Body(string json) => Encoding.UTF8.GetBytes(json);

    private static string Collection(Escrow e, string id = "evt-1", long? amount = null) =>
        $"{{\"eventId\":\"{id}\",\"kind\":\"collection\",\"reference\":\"{e.PaymentReference}\",\"status\":\"succeeded\",\"amount\":{amount ?? e.Total},\"currency\":\"MWK\"}}";

    [Fact]
    public async Task A_bad_signature_is_refused_and_nothing_is_stored()
    {
        var env = await TestEnv.NewAsync();
        var e = await env.PendingOrderAsync();
        var (receipt, _) = await env.Webhooks.ReceiveAsync(Body(Collection(e)), "wrong", null);
        Assert.Equal(WebhookReceipt.Unauthorized, receipt);
        Assert.Equal(0, env.Db.Inbox.Rows.Count);
    }

    [Fact]
    public async Task A_signed_callback_is_stored_raw_then_funds_the_order()
    {
        var env = await TestEnv.NewAsync();
        var e = await env.PendingOrderAsync();
        var json = Collection(e);

        var (receipt, id) = await env.Webhooks.ReceiveAsync(Body(json), env.Provider.Sign(json), null);
        Assert.Equal(WebhookReceipt.Stored, receipt);
        Assert.Equal(json, env.Db.Inbox.Rows.Single().RawPayload);
        Assert.Equal(EscrowState.PaymentPending, e.State);           // stored first, applied next

        Assert.Equal("Processed", await env.Webhooks.ProcessAsync(id!.Value));
        Assert.Equal(EscrowState.Funded, e.State);
        Assert.Equal(WebhookStatus.Processed, env.Db.Inbox.Rows.Single().Status);
    }

    [Fact]
    public async Task A_replayed_callback_is_recognised_and_applied_once()
    {
        var env = await TestEnv.NewAsync();
        var e = await env.PendingOrderAsync();
        var json = Collection(e);
        var sig = env.Provider.Sign(json);

        var (_, id) = await env.Webhooks.ReceiveAsync(Body(json), sig, null);
        await env.Webhooks.ProcessAsync(id!.Value);
        var (again, _) = await env.Webhooks.ReceiveAsync(Body(json), sig, null);

        Assert.Equal(WebhookReceipt.Duplicate, again);
        Assert.Equal(1, env.Db.Inbox.Rows.Count);
        Assert.Equal(1, env.Db.Ledger.Rows.Count);
        Assert.Equal("Processed", await env.Webhooks.ProcessAsync(id.Value));   // re-processing a finished item is a no-op
        Assert.Equal(1, env.Db.Ledger.Rows.Count);
    }

    [Fact]
    public async Task Two_deliveries_racing_past_the_existence_check_are_stopped_by_the_unique_event_key()
    {
        var env = await TestEnv.NewAsync();
        var e = await env.PendingOrderAsync();
        var json = Collection(e);
        env.Db.Inbox.Add(WebhookInboxItem.Create("fake", "evt-1", "", json, TestEnv.Start));
        await env.Db.CommitAsync(default);

        // A second request that did not see the first row yet (it checked before the commit) tries to insert the same key.
        env.Db.Inbox.Add(WebhookInboxItem.Create("fake", "evt-1", "", json, TestEnv.Start));
        await Assert.ThrowsAsync<DuplicateOperationException>(() => env.Db.CommitAsync(default));
        Assert.Equal(1, env.Db.Inbox.Rows.Count);
    }

    [Fact]
    public async Task Events_we_do_not_understand_are_acknowledged_but_not_stored()
    {
        var env = await TestEnv.NewAsync();
        const string json = "{\"hello\":\"world\"}";
        var (receipt, _) = await env.Webhooks.ReceiveAsync(Body(json), env.Provider.Sign(json), null);
        Assert.Equal(WebhookReceipt.Ignored, receipt);
        Assert.Equal(0, env.Db.Inbox.Rows.Count);
    }

    [Fact]
    public async Task A_failing_item_is_kept_for_retry_and_eventually_parked()
    {
        var env = await TestEnv.NewAsync();
        var e = await env.PendingOrderAsync();
        var json = Collection(e);
        var (_, id) = await env.Webhooks.ReceiveAsync(Body(json), env.Provider.Sign(json), null);

        await env.Webhooks.RecordFailureAsync(id!.Value, "database timeout");
        var item = env.Db.Inbox.Rows.Single();
        Assert.Equal(WebhookStatus.Pending, item.Status);      // still retried by the worker
        Assert.Equal(1, item.Attempts);
        Assert.Equal(1, (await env.Webhooks.ListPendingAsync(10)).Count);

        for (var i = 0; i < 9; i++) await env.Webhooks.RecordFailureAsync(id.Value, "database timeout");
        Assert.Equal(WebhookStatus.Failed, item.Status);
        Assert.Equal(1, (await env.Alerts.GetAsync()).WebhooksUnprocessed + (env.Db.Inbox.Rows.Count(i => i.Status == WebhookStatus.Failed)));
    }

    [Fact]
    public async Task A_payout_callback_confirms_the_payout_through_the_same_inbox()
    {
        var env = await TestEnv.NewAsync();
        var e = await env.ReleasedOrderAsync();
        await env.Payouts.ExecuteDueAsync();
        var seller = env.Db.Payouts.Rows.Single(p => p.Kind == PayoutKind.Seller);
        var json = $"{{\"eventId\":\"p-1\",\"kind\":\"payout\",\"reference\":\"{seller.Reference}\",\"status\":\"succeeded\",\"amount\":{seller.Amount},\"fee\":100}}";

        var (_, id) = await env.Webhooks.ReceiveAsync(Body(json), env.Provider.Sign(json), null);
        await env.Webhooks.ProcessAsync(id!.Value);

        Assert.Equal(PayoutStatus.Confirmed, seller.Status);
        Assert.Equal(100, seller.Fee);
    }
}

public class SecurityTests
{
    [Fact]
    public void Access_tokens_round_trip_and_reject_tampering_and_expiry()
    {
        var clock = new TestClock(TestEnv.Start);
        var tokens = new AccessTokens(new AuthOptions { TokenLifetimeHours = 12 }, clock);
        var (token, expires) = tokens.Issue("courier-1", Roles.Courier, "Mphatso");

        var principal = tokens.Validate(token)!;
        Assert.Equal("courier-1", principal.Subject);
        Assert.Equal(Roles.Courier, principal.Role);
        Assert.Equal(expires, principal.ExpiresAt);

        var parts = token.Split('.');
        var forged = Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"sub\":\"x\",\"role\":\"ops_admin\",\"name\":\"\",\"exp\":9999999999}")).TrimEnd('=') + "." + parts[1];
        Assert.Null(tokens.Validate(forged));
        Assert.Null(tokens.Validate(token + "x"));
        Assert.Null(tokens.Validate("garbage"));
        Assert.Null(tokens.Validate(null));

        clock.Advance(TimeSpan.FromHours(13));
        Assert.Null(tokens.Validate(token));
        Assert.Null(new AccessTokens(new AuthOptions { TokenSecret = "a-completely-different-secret-of-32-chars!!" }, new TestClock(TestEnv.Start)).Validate(token));
    }

    [Fact]
    public void A_short_signing_secret_is_refused()
    {
        Assert.Throws<InvalidOperationException>(() => new AccessTokens(new AuthOptions { TokenSecret = "short" }, TimeProvider.System));
    }

    [Fact]
    public void Pins_are_salted_hashed_and_validated()
    {
        var a = PinHasher.Hash("4821");
        var b = PinHasher.Hash("4821");
        Assert.NotEqual(a, b);                         // random salt
        Assert.False(a.Contains("4821"));
        Assert.True(PinHasher.Verify("4821", a));
        Assert.False(PinHasher.Verify("4822", a));
        Assert.False(PinHasher.Verify("4821", "garbage"));
        Assert.Throws<DomainValidationException>(() => PinHasher.Hash("12"));
        Assert.Throws<DomainValidationException>(() => PinHasher.Hash("abcd"));
    }

    [Fact]
    public void Encryption_round_trips_and_detects_tampering()
    {
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var protector = new AesGcmProtector(new SecurityOptions { DataKey = key });
        var sealedValue = protector.Protect("481 902");
        Assert.NotEqual(sealedValue, protector.Protect("481 902"));    // fresh nonce each time
        Assert.Equal("481 902", protector.Unprotect(sealedValue));

        var bytes = Convert.FromBase64String(sealedValue);
        bytes[^1] ^= 0x01;
        Assert.ThrowsAny<Exception>(() => protector.Unprotect(Convert.ToBase64String(bytes)));
        Assert.Throws<InvalidOperationException>(() => new AesGcmProtector(new SecurityOptions { DataKey = Convert.ToBase64String(new byte[16]) }));
        Assert.Throws<InvalidOperationException>(() => new AesGcmProtector(new SecurityOptions { DataKey = "not base64!" }));
        var missingKey = Assert.Throws<InvalidOperationException>(() => new AesGcmProtector(new SecurityOptions()));
        Assert.Contains("Security__DataKey", missingKey.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Upload_links_are_signed_expire_and_cannot_be_reused_for_another_slot()
    {
        var storage = new LocalObjectStorage(new StorageOptions { PublicBaseUrl = "http://api.test", SigningSecret = "s", RootPath = Path.Combine(Path.GetTempPath(), "ce-test-" + Guid.NewGuid()) });
        var id = Guid.NewGuid();
        var target = storage.CreateUploadTarget(id, TestEnv.Start);
        var query = System.Web.HttpUtility.ParseQueryString(new Uri(target.Url).Query);
        var exp = long.Parse(query["exp"]!);
        var sig = query["sig"];

        Assert.True(storage.ValidateUploadTarget(id, exp, sig, TestEnv.Start.AddMinutes(5)));
        Assert.False(storage.ValidateUploadTarget(id, exp, sig, TestEnv.Start.AddMinutes(11)));          // expired
        Assert.False(storage.ValidateUploadTarget(Guid.NewGuid(), exp, sig, TestEnv.Start));              // other slot
        Assert.False(storage.ValidateUploadTarget(id, exp + 600, sig, TestEnv.Start));                    // extended expiry
        Assert.False(storage.ValidateUploadTarget(id, exp, null, TestEnv.Start));
    }

    [Fact]
    public async Task Stored_photos_cannot_escape_the_storage_folder()
    {
        var root = Path.Combine(Path.GetTempPath(), "ce-test-" + Guid.NewGuid());
        var storage = new LocalObjectStorage(new StorageOptions { RootPath = root });
        await storage.SaveAsync("proofs/a", new MemoryStream([1, 2, 3]), default);
        await using (var s = (await storage.OpenAsync("proofs/a", default))!) Assert.Equal(3, s.Length);
        await Assert.ThrowsAsync<InvalidOperationException>(() => storage.SaveAsync("../../etc/evil", new MemoryStream([1]), default));
        Directory.Delete(root, true);
    }
}

public class CourierLoginTests
{
    [Fact]
    public async Task Signing_in_binds_the_device_and_returns_a_working_token()
    {
        var env = await TestEnv.NewAsync();
        var login = await env.Couriers.LoginAsync("+265 999 000 111", "4821", "device-A");

        Assert.Equal(env.Courier.Id, login.CourierId);
        var principal = env.Tokens.Validate(login.Token)!;
        Assert.Equal(Roles.Courier, principal.Role);
        Assert.Equal(env.Courier.Id.ToString(), principal.Subject);
        Assert.Equal("device-A", env.Courier.DeviceId);
    }

    [Fact]
    public async Task Wrong_phone_or_pin_gives_the_same_answer()
    {
        var env = await TestEnv.NewAsync();
        var a = await Assert.ThrowsAsync<AuthenticationFailedException>(() => env.Couriers.LoginAsync("0999000111", "0000", "d"));
        var b = await Assert.ThrowsAsync<AuthenticationFailedException>(() => env.Couriers.LoginAsync("0999999999", "4821", "d"));
        var c = await Assert.ThrowsAsync<AuthenticationFailedException>(() => env.Couriers.LoginAsync("garbage", "4821", "d"));
        Assert.Equal(a.Message, b.Message);
        Assert.Equal(a.Message, c.Message);
    }

    [Fact]
    public async Task Five_wrong_pins_lock_the_account_even_for_the_right_pin_until_the_lock_ends()
    {
        var env = await TestEnv.NewAsync();
        for (var i = 0; i < 5; i++) await Assert.ThrowsAsync<AuthenticationFailedException>(() => env.Couriers.LoginAsync("0999000111", "0000", "d"));

        await Assert.ThrowsAsync<TooManyAttemptsException>(() => env.Couriers.LoginAsync("0999000111", "4821", "d"));
        env.Clock.Advance(TimeSpan.FromMinutes(16));
        Assert.NotNull(await env.Couriers.LoginAsync("0999000111", "4821", "d"));
    }

    [Fact]
    public async Task A_new_phone_is_refused_until_ops_resets_the_device()
    {
        var env = await TestEnv.NewAsync();
        await env.Couriers.LoginAsync("0999000111", "4821", "device-A");
        await Assert.ThrowsAsync<ForbiddenException>(() => env.Couriers.LoginAsync("0999000111", "4821", "device-B"));

        await env.Couriers.ResetDeviceAsync(env.Courier.Id, "staff:admin");
        Assert.NotNull(await env.Couriers.LoginAsync("0999000111", "4821", "device-B"));
    }

    [Fact]
    public async Task A_suspended_courier_cannot_sign_in_or_be_assigned_new_orders()
    {
        var env = await TestEnv.NewAsync();
        await env.Couriers.SetStatusAsync(env.Courier.Id, false, "staff:admin");
        await Assert.ThrowsAsync<AuthenticationFailedException>(() => env.Couriers.LoginAsync("0999000111", "4821", "d"));
        await Assert.ThrowsAsync<DomainValidationException>(() => env.CreateOrderAsync());
    }

    [Fact]
    public async Task Duplicate_courier_phones_are_refused_and_the_job_list_is_scoped_to_the_courier()
    {
        var env = await TestEnv.NewAsync();
        await Assert.ThrowsAsync<DomainValidationException>(() => env.Couriers.CreateAsync("Dup", "0999000111", MobileNetwork.Airtel.ToString(), "1234", "staff:admin"));

        var funded = await env.FundedOrderAsync();
        var other = await env.Couriers.CreateAsync("Other", "0888555444", MobileNetwork.Tnm.ToString(), "1234", "staff:admin");
        Assert.Equal(1, (await env.Couriers.GetJobsAsync(env.Courier.Id)).Count);
        Assert.Equal(0, (await env.Couriers.GetJobsAsync(other.Id)).Count);
        await Assert.ThrowsAsync<NotFoundException>(() => env.Couriers.GetJobAsync(other.Id, funded.Id));
        var job = await env.Couriers.GetJobAsync(env.Courier.Id, funded.Id);
        Assert.Equal("Chikondi Phones", job.Seller.Name);
    }
}
