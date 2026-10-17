using System.Security.Cryptography;
using CourierEscrow.Application;
using CourierEscrow.Application.Services;
using CourierEscrow.Domain;
using CourierEscrow.Tests.Fakes;

namespace CourierEscrow.Tests;

public class HandoffTests
{
    private static readonly GeoPoint Gps = new(-13.97, 33.78);

    [Fact]
    public async Task Photo_uploads_are_hashed_by_the_server_and_validated()
    {
        var env = await TestEnv.NewAsync();
        var slot = await env.Handoff.CreateUploadSlotAsync(env.Courier.Id, "image/jpeg");
        var bytes = new byte[] { 9, 8, 7, 6 };

        await Assert.ThrowsAsync<ForbiddenException>(() => env.Handoff.StoreUploadAsync(slot.UploadId, 0, "bad-signature", new MemoryStream(bytes)));
        await env.Handoff.StoreUploadAsync(slot.UploadId, 0, "ok", new MemoryStream(bytes));

        var upload = env.Db.Uploads.Rows.Single();
        Assert.True(upload.Stored);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), upload.Sha256);
        Assert.Equal(4, env.Storage.Objects[upload.ObjectKey].Length);
        await Assert.ThrowsAsync<DomainValidationException>(() => env.Handoff.StoreUploadAsync(slot.UploadId, 0, "ok", new MemoryStream(bytes))); // slot is single-use
        await Assert.ThrowsAsync<DomainValidationException>(() => env.Handoff.CreateUploadSlotAsync(env.Courier.Id, "application/pdf"));
    }

    [Fact]
    public async Task Pickup_records_proof_issues_a_hashed_otp_and_texts_buyer_and_seller()
    {
        var env = await TestEnv.NewAsync();
        var e = await env.FundedOrderAsync();

        var result = await env.PickupAsync(e);

        Assert.Equal(EscrowState.PickedUp, result.State);
        Assert.Equal("SL-558201", e.SealId);
        Assert.Equal(600, e.WeightGrams);
        Assert.Equal(3, env.Db.Proofs.Rows.Count(p => p.Kind == ProofKind.Pickup));
        Assert.True(env.Db.Proofs.Rows.All(p => p.Sha256.Length == 64));

        var code = env.LastOtp();
        var otp = env.Db.Otps.Rows.Single();
        Assert.False(otp.Hmac.Contains(code));
        Assert.Equal(env.Start24h(), otp.ExpiresAt);

        var buyerSms = env.Db.Notifications.Rows.Single(n => n.Template == "picked_up_buyer");
        Assert.True(buyerSms.Sensitive);
        Assert.True(buyerSms.Body.StartsWith("enc:"));            // never stored in clear
        Assert.Contains($"nexa.mw/t/{e.TrackingToken}", env.Protector.Unprotect(buyerSms.Body));
        var sellerSms = env.Db.Notifications.Rows.Single(n => n.Template == "dispatch_proof_seller");
        Assert.Contains("SL-558201", sellerSms.Body);
        Assert.Contains("3 photos", sellerSms.Body);
    }

    [Fact]
    public async Task Pickup_with_too_few_photos_is_refused_and_changes_nothing()
    {
        var env = await TestEnv.NewAsync();
        var e = await env.FundedOrderAsync();
        await Assert.ThrowsAsync<DomainValidationException>(() => env.PickupAsync(e, photos: 2));
        Assert.Equal(EscrowState.Funded, e.State);
        Assert.Equal(0, env.Db.Otps.Rows.Count);
    }

    [Fact]
    public async Task A_retried_pickup_with_the_same_client_event_id_is_harmless()
    {
        var env = await TestEnv.NewAsync();
        var e = await env.FundedOrderAsync();
        var cmd = new PickupCommand("ev-1", await env.PhotosAsync(3), 0.6m, "SL-1", Gps, null);

        var first = await env.Handoff.PickupAsync(env.Courier.Id, e.Id, cmd);
        var smsCount = env.Db.Notifications.Rows.Count;
        var second = await env.Handoff.PickupAsync(env.Courier.Id, e.Id, cmd);

        Assert.False(first.Replayed);
        Assert.True(second.Replayed);
        Assert.Equal(EscrowState.PickedUp, second.State);
        Assert.Equal(smsCount, env.Db.Notifications.Rows.Count);
        Assert.Equal(1, env.Db.Otps.Rows.Count);
        Assert.Equal(3, env.Db.Proofs.Rows.Count(p => p.Kind == ProofKind.Pickup));   // the replay added nothing
    }

    [Fact]
    public async Task A_courier_cannot_touch_another_couriers_job_or_photos()
    {
        var env = await TestEnv.NewAsync();
        var e = await env.FundedOrderAsync();
        var other = await env.Couriers.CreateAsync("Other Rider", "0888555444", MobileNetwork.Tnm.ToString(), "1234", "staff:ops");

        await Assert.ThrowsAsync<ForbiddenException>(() => env.Handoff.PickupAsync(other.Id, e.Id,
            new PickupCommand("x", [Guid.NewGuid()], 0.6m, "SL", Gps, null)));

        var theirPhotos = await env.PhotosAsync(3);
        var cmd = new PickupCommand("y", theirPhotos, 0.6m, "SL", Gps, null);
        await Assert.ThrowsAsync<DomainValidationException>(() => env.Handoff.PickupAsync(env.Courier.Id, e.Id, cmd with { UploadIds = [.. theirPhotos.Take(2), Guid.NewGuid()] }));
    }

    [Fact]
    public async Task A_photo_that_never_finished_uploading_cannot_be_used()
    {
        var env = await TestEnv.NewAsync();
        var e = await env.FundedOrderAsync();
        var ready = await env.PhotosAsync(2);
        var empty = await env.Handoff.CreateUploadSlotAsync(env.Courier.Id, "image/png");
        await Assert.ThrowsAsync<DomainValidationException>(() => env.Handoff.PickupAsync(env.Courier.Id, e.Id,
            new PickupCommand("z", [.. ready, empty.UploadId], 0.6m, "SL", Gps, null)));
    }

    [Fact]
    public async Task Arrival_needs_a_photo_and_gps_and_starts_the_inspection()
    {
        var env = await TestEnv.NewAsync();
        var e = await env.PickedUpOrderAsync();
        await Assert.ThrowsAsync<DomainValidationException>(() => env.Handoff.ArriveAsync(env.Courier.Id, e.Id, new ArrivalCommand("a1", [], Gps, null)));
        var photo = await env.PhotosAsync(1);
        await Assert.ThrowsAsync<DomainValidationException>(() => env.Handoff.ArriveAsync(env.Courier.Id, e.Id, new ArrivalCommand("a2", photo, null, null)));

        await env.Handoff.ArriveAsync(env.Courier.Id, e.Id, new ArrivalCommand("a3", await env.PhotosAsync(1), Gps, null));
        Assert.Equal(EscrowState.Delivered, e.State);
        Assert.NotNull(e.DeliveredAt);
    }

    [Fact]
    public async Task The_right_code_releases_the_money_and_queues_seller_and_courier_payouts()
    {
        var env = await TestEnv.NewAsync();
        var e = await env.DeliveredOrderAsync();

        var r = await env.Handoff.SubmitOtpAsync(env.Courier.Id, e.Id, "otp-1", env.LastOtp());

        Assert.Equal(OtpResult.Accepted, r.Result);
        Assert.Equal(EscrowState.Released, e.State);
        Assert.Equal(0, env.Held(e));

        var seller = env.Db.Payouts.Rows.Single(p => p.Kind == PayoutKind.Seller);
        var courier = env.Db.Payouts.Rows.Single(p => p.Kind == PayoutKind.Courier);
        Assert.Equal(29_400_000, seller.Amount);
        Assert.Equal("265991234567", seller.Msisdn);
        Assert.Equal(MobileNetwork.Airtel.ToString(), seller.Network);
        Assert.Equal(800_000, courier.Amount);
        Assert.Equal(2, env.Db.Payouts.Rows.Count);

        var summary = await env.Repos.GetSummaryAsync("MWK", default);
        Assert.Equal(600_000, summary.PlatformRevenue);
        Assert.Equal(29_400_000, summary.OwedToSellers);
        Assert.Equal(800_000, summary.OwedToCouriers);
        Assert.True(summary.IsBalanced);

        Assert.Contains("K294,000", env.Db.Notifications.Rows.Single(n => n.Template == "released_seller").Body);
        Assert.Contains("2% fee K6,000", env.Db.Notifications.Rows.Single(n => n.Template == "released_seller").Body);
    }

    [Fact]
    public async Task The_code_is_refused_before_the_courier_has_arrived()
    {
        var env = await TestEnv.NewAsync();
        var e = await env.PickedUpOrderAsync();
        await Assert.ThrowsAsync<InvalidEscrowTransitionException>(() => env.Handoff.SubmitOtpAsync(env.Courier.Id, e.Id, "o", env.LastOtp()));
    }

    [Fact]
    public async Task A_wrong_code_counts_down_and_a_retry_of_the_same_attempt_is_not_counted_twice()
    {
        var env = await TestEnv.NewAsync();
        var e = await env.DeliveredOrderAsync();

        var first = await env.Handoff.SubmitOtpAsync(env.Courier.Id, e.Id, "w1", "000000");
        Assert.Equal(OtpResult.Wrong, first.Result);
        Assert.Equal(4, first.AttemptsLeft);

        var retry = await env.Handoff.SubmitOtpAsync(env.Courier.Id, e.Id, "w1", "000000");
        Assert.True(retry.Replayed);
        Assert.Equal(1, env.Db.Otps.Rows.Single().Attempts);

        var second = await env.Handoff.SubmitOtpAsync(env.Courier.Id, e.Id, "w2", "000001");
        Assert.Equal(3, second.AttemptsLeft);
        Assert.Equal(EscrowState.Delivered, e.State);
    }

    [Fact]
    public async Task Five_wrong_codes_lock_the_order_and_send_it_to_ops()
    {
        var env = await TestEnv.NewAsync();
        var e = await env.DeliveredOrderAsync();
        var real = env.LastOtp();

        OtpOutcome last = null!;
        for (var i = 1; i <= 5; i++) last = await env.Handoff.SubmitOtpAsync(env.Courier.Id, e.Id, $"w{i}", "000000");

        Assert.Equal(OtpResult.Locked, last.Result);
        Assert.Contains("OTP locked", e.OpsFlag);
        var guess = await env.Handoff.SubmitOtpAsync(env.Courier.Id, e.Id, "w6", real);
        Assert.Equal(OtpResult.Locked, guess.Result);          // even the right code no longer works
        Assert.Equal(EscrowState.Delivered, e.State);
        Assert.Equal(0, env.Db.Payouts.Rows.Count);
    }

    [Fact]
    public async Task Ops_can_reissue_a_locked_code_and_the_old_one_stops_working()
    {
        var env = await TestEnv.NewAsync();
        var e = await env.DeliveredOrderAsync();
        var old = env.LastOtp();
        for (var i = 1; i <= 5; i++) await env.Handoff.SubmitOtpAsync(env.Courier.Id, e.Id, $"w{i}", "000000");

        await env.Handoff.ReissueOtpAsync(e.Id, "staff:ops");
        var fresh = env.LastOtp();
        Assert.Null(e.OpsFlag);

        if (fresh != old) Assert.Equal(OtpResult.Wrong, (await env.Handoff.SubmitOtpAsync(env.Courier.Id, e.Id, "old", old)).Result);
        Assert.Equal(OtpResult.Accepted, (await env.Handoff.SubmitOtpAsync(env.Courier.Id, e.Id, "new", fresh)).Result);
        Assert.Equal(EscrowState.Released, e.State);
        Assert.True(env.Db.Audit.Rows.Any(a => a.Action == "otp.reissue"));
    }

    [Fact]
    public async Task An_expired_code_is_flagged_and_can_be_reissued()
    {
        var env = await TestEnv.NewAsync();
        var e = await env.DeliveredOrderAsync();
        var code = env.LastOtp();
        env.Clock.Advance(TimeSpan.FromHours(25));

        var r = await env.Handoff.SubmitOtpAsync(env.Courier.Id, e.Id, "late", code);

        Assert.Equal(OtpResult.Expired, r.Result);
        Assert.Contains("expired", e.OpsFlag);
        await env.Handoff.ReissueOtpAsync(e.Id, "staff:ops");
        Assert.Equal(OtpResult.Accepted, (await env.Handoff.SubmitOtpAsync(env.Courier.Id, e.Id, "ok", env.LastOtp())).Result);
    }

    [Fact]
    public async Task Rejecting_at_the_door_freezes_the_funds_and_tells_ops_and_the_seller()
    {
        var env = await TestEnv.NewAsync();
        var e = await env.DeliveredOrderAsync();

        await env.Handoff.RejectAsync(env.Courier.Id, e.Id, new RejectCommand("r1", "wrong colour", await env.PhotosAsync(2), Gps, null));

        Assert.Equal(EscrowState.Rejected, e.State);
        Assert.Equal(TestEnv.Total, env.Held(e));
        Assert.Contains("wrong colour", e.OpsFlag);
        Assert.Contains("wrong colour", env.Db.Notifications.Rows.Single(n => n.Template == "rejected_seller").Body);
        Assert.Equal(2, env.Db.Proofs.Rows.Count(p => p.Kind == ProofKind.Rejection));
        await Assert.ThrowsAsync<DomainValidationException>(() => env.Handoff.RejectAsync(env.Courier.Id, e.Id, new RejectCommand("r2", "x", [], Gps, null)));
    }

    [Fact]
    public async Task Reject_return_then_ops_refund_gives_the_buyer_the_item_and_the_courier_the_delivery_fee()
    {
        var env = await TestEnv.NewAsync();
        var e = await env.DeliveredOrderAsync();
        await env.Handoff.RejectAsync(env.Courier.Id, e.Id, new RejectCommand("r1", "damaged", await env.PhotosAsync(2), Gps, null));
        await env.Handoff.ReturnAsync(env.Courier.Id, e.Id, new ReturnCommand("ret1", await env.PhotosAsync(1), true, Gps, null));
        Assert.Equal(EscrowState.Returned, e.State);

        await env.Orders.RefundAsync(e.Id, "staff:ops", "item returned");

        Assert.Equal(EscrowState.Refunded, e.State);
        Assert.Equal(0, env.Held(e));
        Assert.Equal(30_000_000, env.Db.Payouts.Rows.Single(p => p.Kind == PayoutKind.BuyerRefund).Amount);
        Assert.Equal(800_000, env.Db.Payouts.Rows.Single(p => p.Kind == PayoutKind.Courier).Amount);
        Assert.False(env.Db.Payouts.Rows.Any(p => p.Kind == PayoutKind.Seller));
        Assert.Equal(0, (await env.Repos.GetSummaryAsync("MWK", default)).PlatformRevenue);
        Assert.Null(e.OpsFlag);
    }

    [Fact]
    public async Task The_return_needs_the_sellers_confirmation()
    {
        var env = await TestEnv.NewAsync();
        var e = await env.DeliveredOrderAsync();
        await env.Handoff.RejectAsync(env.Courier.Id, e.Id, new RejectCommand("r1", "damaged", await env.PhotosAsync(2), Gps, null));
        var photo = await env.PhotosAsync(1);
        await Assert.ThrowsAsync<DomainValidationException>(() => env.Handoff.ReturnAsync(env.Courier.Id, e.Id, new ReturnCommand("ret1", photo, false, Gps, null)));
    }

    [Fact]
    public async Task An_unreachable_buyer_flags_the_order_without_changing_its_state()
    {
        var env = await TestEnv.NewAsync();
        var e = await env.DeliveredOrderAsync();
        await env.Handoff.FlagUnreachableAsync(env.Courier.Id, e.Id, "u1");
        Assert.Equal(EscrowState.Delivered, e.State);
        Assert.Contains("unreachable", e.OpsFlag);
        Assert.Equal(TestEnv.Total, env.Held(e));
    }

    [Fact]
    public async Task Refunding_a_funded_order_returns_everything_and_pays_no_courier()
    {
        var env = await TestEnv.NewAsync();
        var e = await env.FundedOrderAsync();
        await env.Orders.RefundAsync(e.Id, "staff:ops", "seller out of stock");
        Assert.Equal(TestEnv.Total, env.Db.Payouts.Rows.Single().Amount);
        Assert.Equal(PayoutKind.BuyerRefund, env.Db.Payouts.Rows.Single().Kind);
        Assert.Equal(0, env.Held(e));
    }
}

internal static class EnvExtensions
{
    public static DateTimeOffset Start24h(this TestEnv env) => TestEnv.Start.AddHours(env.Options.OtpLifetimeHours);
}
