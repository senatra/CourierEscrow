using System.Security.Cryptography;
using CourierEscrow.Application.Helpers;
using CourierEscrow.Application.Interfaces;
using CourierEscrow.Application.Services.Support;
using CourierEscrow.Domain;
using CourierEscrow.Domain.Models;

namespace CourierEscrow.Application.Services;

public sealed record PickupCommand(string ClientEventId, IReadOnlyList<Guid> UploadIds, decimal WeightKg, string? SealId, GeoPoint? Gps, DateTimeOffset? ClientTime);
public sealed record ArrivalCommand(string ClientEventId, IReadOnlyList<Guid> UploadIds, GeoPoint? Gps, DateTimeOffset? ClientTime);
public sealed record RejectCommand(string ClientEventId, string ReasonCode, IReadOnlyList<Guid> UploadIds, GeoPoint? Gps, DateTimeOffset? ClientTime);
public sealed record ReturnCommand(string ClientEventId, IReadOnlyList<Guid> UploadIds, bool SellerConfirmed, GeoPoint? Gps, DateTimeOffset? ClientTime);

/// <param name="Replayed">The same client event id was already applied, so nothing changed.</param>
public sealed record HandoffResult(EscrowState State, bool Replayed);
public sealed record OtpOutcome(OtpResult Result, int AttemptsLeft, EscrowState State, bool Replayed);
public sealed record UploadSlot(Guid UploadId, string Url, DateTimeOffset ExpiresAt);

/// <summary>
/// The courier's side of an order: photos, pickup proof, arrival, the buyer's code, rejection and return.
/// Every call carries a client event id, so a retry from a weak network is recognised and harmless.
/// </summary>
public sealed class HandoffService(
    IEscrowRepository escrows, IPartyRepository parties, ICourierRepository couriers, IUploadRepository uploads,
    IProofRepository proofs, IOtpRepository otps, IOrderEventStore orderEvents, IObjectStorage storage, IAuditLog audit,
    PayoutService payoutService, EscrowCommit commit, Notifier notifier, TimeProvider clock, EscrowOptions options)
{
    public const long MaxPhotoBytes = 10 * 1024 * 1024;
    private static readonly string[] PhotoTypes = ["image/jpeg", "image/png", "image/webp"];

    public Task<UploadSlot> CreateUploadSlotAsync(Guid courierId, string contentType, CancellationToken ct = default) =>
        CreateUploadSlotAsync((Guid?)courierId, contentType, ct);

    /// <summary>Ops listing photos: not bound to a courier, so they cannot be reused as pickup proof.</summary>
    public Task<UploadSlot> CreateStaffUploadSlotAsync(string contentType, CancellationToken ct = default) =>
        CreateUploadSlotAsync(null, contentType, ct);

    private async Task<UploadSlot> CreateUploadSlotAsync(Guid? courierId, string contentType, CancellationToken ct)
    {
        if (!PhotoTypes.Contains(contentType, StringComparer.OrdinalIgnoreCase))
            throw new DomainValidationException("Photos must be JPEG, PNG or WebP.");
        var now = clock.GetUtcNow();
        var upload = Upload.Create(courierId, contentType.ToLowerInvariant(), now);
        uploads.Add(upload);
        await commit.SaveAsync(ct);
        var target = storage.CreateUploadTarget(upload.Id, now);
        return new UploadSlot(upload.Id, target.Url, target.ExpiresAt);
    }

    /// <summary>Called by the upload endpoint. The server computes the hash itself, so the client cannot lie about it.</summary>
    public async Task StoreUploadAsync(Guid uploadId, long expiresUnix, string? signature, Stream content, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        if (!storage.ValidateUploadTarget(uploadId, expiresUnix, signature, now)) throw new ForbiddenException("The upload link is invalid or expired.");
        var upload = await uploads.GetAsync(uploadId, ct) ?? throw new NotFoundException("Upload slot not found.");

        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await content.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaxPhotoBytes) throw new DomainValidationException("Photo is larger than 10 MB.");
            buffer.Write(chunk, 0, read);
        }
        if (buffer.Length == 0) throw new DomainValidationException("The photo is empty.");

        var bytes = buffer.ToArray();
        buffer.Position = 0;
        await storage.SaveAsync(upload.ObjectKey, buffer, ct);
        upload.MarkStored(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), bytes.Length, now);
        await commit.SaveAsync(ct);
    }

    public async Task<HandoffResult> PickupAsync(Guid courierId, Guid escrowId, PickupCommand cmd, CancellationToken ct = default)
    {
        var escrow = await LoadForCourierAsync(courierId, escrowId, ct);
        if (await Seen(escrowId, cmd.ClientEventId, ct)) return new(escrow.State, true);

        var now = clock.GetUtcNow();
        var photos = await ResolveUploadsAsync(courierId, cmd.UploadIds, ct);
        var actor = Actors.Courier(courierId);

        escrow.MarkPickedUp(new PickupEvidence(photos.Count, cmd.SealId, cmd.WeightKg, cmd.Gps), actor, now, cmd.ClientEventId);
        foreach (var u in photos) proofs.Add(Proof.Create(escrow, ProofKind.Pickup, u, cmd.Gps, cmd.ClientTime, now));

        var code = OtpHasher.NewCode();
        otps.Add(Otp.Issue(escrow.Id, options.OtpSecret, code, now.AddHours(options.OtpLifetimeHours)));

        var (buyer, seller) = await GetPartiesAsync(escrow, ct);
        notifier.Queue(escrow, buyer.Phone, "picked_up_buyer", SmsTemplates.PickedUpBuyer(escrow, code, TrackUrl(escrow)), sensitive: true);
        notifier.Queue(escrow, seller.Phone, "dispatch_proof_seller", SmsTemplates.DispatchProofSeller(escrow, now, photos.Count));
        await commit.SaveAsync(ct, escrow);
        return new(escrow.State, false);
    }

    public async Task<HandoffResult> ArriveAsync(Guid courierId, Guid escrowId, ArrivalCommand cmd, CancellationToken ct = default)
    {
        var escrow = await LoadForCourierAsync(courierId, escrowId, ct);
        if (await Seen(escrowId, cmd.ClientEventId, ct)) return new(escrow.State, true);

        var now = clock.GetUtcNow();
        var photos = await ResolveUploadsAsync(courierId, cmd.UploadIds, ct);
        escrow.MarkArrived(new ArrivalEvidence(photos.Count, cmd.Gps), Actors.Courier(courierId), now, cmd.ClientEventId);
        foreach (var u in photos) proofs.Add(Proof.Create(escrow, ProofKind.Arrival, u, cmd.Gps, cmd.ClientTime, now));
        await commit.SaveAsync(ct, escrow);
        return new(escrow.State, false);
    }

    /// <summary>The courier types the code the buyer gave after inspecting. Wrong attempts are counted and persisted.</summary>
    public async Task<OtpOutcome> SubmitOtpAsync(Guid courierId, Guid escrowId, string clientEventId, string code, CancellationToken ct = default)
    {
        var escrow = await LoadForCourierAsync(courierId, escrowId, ct);
        return await VerifyCodeAsync(escrow, Actors.Courier(courierId), clientEventId, code, ct);
    }

    private async Task<OtpOutcome> VerifyCodeAsync(Escrow escrow, string actor, string clientEventId, string code, CancellationToken ct)
    {
        var escrowId = escrow.Id;
        if (await Seen(escrowId, clientEventId, ct)) return new(OtpResult.NotActive, 0, escrow.State, true);
        if (escrow.State != EscrowState.Delivered) throw new InvalidEscrowTransitionException(escrow.State, "accept the buyer code");

        var now = clock.GetUtcNow();
        var otp = await otps.GetAsync(escrowId, ct) ?? throw new NotFoundException("No code was issued for this order.");
        var check = otp.Verify(options.OtpSecret, code, now);

        switch (check.Result)
        {
            case OtpResult.Accepted:
                var settlement = escrow.Release(actor, now, clientEventId);
                await payoutService.StageSettlementAsync(escrow, settlement, "release", ct);
                var (_, seller) = await GetPartiesAsync(escrow, ct);
                var courier = await couriers.GetAsync(escrow.CourierId, ct);
                notifier.Queue(escrow, seller.Phone, "released_seller", SmsTemplates.ReleasedSeller(escrow, settlement));
                if (courier is not null) notifier.Queue(escrow, courier.Phone, "released_courier", SmsTemplates.ReleasedCourier(escrow, settlement));
                break;

            case OtpResult.Wrong:
                escrow.RecordEvent("OtpAttemptFailed", actor, now, clientEventId, new { check.AttemptsLeft });
                break;

            case OtpResult.Locked:
                escrow.RecordEvent("OtpLocked", actor, now, clientEventId);
                escrow.FlagForOps($"OTP locked after {Otp.MaxAttempts} wrong attempts. Ops must verify the buyer and reissue the code.", actor, now);
                break;

            default:
                escrow.RecordEvent("OtpRejected", actor, now, clientEventId, new { result = check.Result.ToString() });
                escrow.FlagForOps("The buyer's code expired. Ops must reissue it.", actor, now);
                break;
        }

        await commit.SaveAsync(ct, escrow);
        return new(check.Result, check.AttemptsLeft, escrow.State, false);
    }

    public async Task<HandoffResult> RejectAsync(Guid courierId, Guid escrowId, RejectCommand cmd, CancellationToken ct = default)
    {
        var escrow = await LoadForCourierAsync(courierId, escrowId, ct);
        if (await Seen(escrowId, cmd.ClientEventId, ct)) return new(escrow.State, true);

        var now = clock.GetUtcNow();
        var photos = await ResolveUploadsAsync(courierId, cmd.UploadIds, ct);
        escrow.Reject(cmd.ReasonCode, photos.Count, cmd.Gps, Actors.Courier(courierId), now, cmd.ClientEventId);
        foreach (var u in photos) proofs.Add(Proof.Create(escrow, ProofKind.Rejection, u, cmd.Gps, cmd.ClientTime, now));

        var (buyer, seller) = await GetPartiesAsync(escrow, ct);
        notifier.Queue(escrow, seller.Phone, "rejected_seller", SmsTemplates.RejectedSeller(escrow, cmd.ReasonCode.Trim()));
        notifier.Queue(escrow, buyer.Phone, "rejected_buyer", SmsTemplates.RejectedBuyer(escrow));
        await commit.SaveAsync(ct, escrow);
        return new(escrow.State, false);
    }

    public async Task<HandoffResult> ReturnAsync(Guid courierId, Guid escrowId, ReturnCommand cmd, CancellationToken ct = default)
    {
        var escrow = await LoadForCourierAsync(courierId, escrowId, ct);
        if (await Seen(escrowId, cmd.ClientEventId, ct)) return new(escrow.State, true);

        var now = clock.GetUtcNow();
        var photos = await ResolveUploadsAsync(courierId, cmd.UploadIds, ct);
        escrow.ConfirmReturn(photos.Count, cmd.SellerConfirmed, Actors.Courier(courierId), now, cmd.ClientEventId);
        foreach (var u in photos) proofs.Add(Proof.Create(escrow, ProofKind.Return, u, cmd.Gps, cmd.ClientTime, now));
        await commit.SaveAsync(ct, escrow);
        return new(escrow.State, false);
    }

    /// <summary>Inspection window problem: the buyer cannot be reached. No auto-release in the MVP; ops decide.</summary>
    public async Task<HandoffResult> FlagUnreachableAsync(Guid courierId, Guid escrowId, string clientEventId, CancellationToken ct = default)
    {
        var escrow = await LoadForCourierAsync(courierId, escrowId, ct);
        if (await Seen(escrowId, clientEventId, ct)) return new(escrow.State, true);
        if (escrow.State is not (EscrowState.PickedUp or EscrowState.Delivered)) throw new InvalidEscrowTransitionException(escrow.State, "flag the buyer as unreachable");

        escrow.FlagForOps("Buyer unreachable: the courier could not hand over. Ops decide.", Actors.Courier(courierId), clock.GetUtcNow());
        escrow.RecordEvent("BuyerUnreachable", Actors.Courier(courierId), clock.GetUtcNow(), clientEventId);
        await commit.SaveAsync(ct, escrow);
        return new(escrow.State, false);
    }

    /// <summary>Ops issue a new code (expired, lost, or locked after wrong attempts). The old code stops working.</summary>
    public async Task ReissueOtpAsync(Guid escrowId, string actor, CancellationToken ct = default)
    {
        var escrow = await escrows.GetAsync(escrowId, ct) ?? throw new NotFoundException("Order not found.");
        if (escrow.State is not (EscrowState.PickedUp or EscrowState.Delivered))
            throw new InvalidEscrowTransitionException(escrow.State, "reissue the buyer code");

        var now = clock.GetUtcNow();
        var code = OtpHasher.NewCode();
        var otp = await otps.GetAsync(escrowId, ct);
        if (otp is null) otps.Add(Otp.Issue(escrowId, options.OtpSecret, code, now.AddHours(options.OtpLifetimeHours)));
        else otp.Reissue(options.OtpSecret, code, now.AddHours(options.OtpLifetimeHours));

        escrow.RecordEvent("OtpReissued", actor, now, null);
        escrow.ClearOpsFlag(actor, now);
        audit.Add(AuditEntry.Create(actor, "otp.reissue", "order", escrowId.ToString(), null, now));

        var (buyer, _) = await GetPartiesAsync(escrow, ct);
        notifier.Queue(escrow, buyer.Phone, "otp_reissued", SmsTemplates.OtpReissued(escrow, code, TrackUrl(escrow)), sensitive: true);
        await commit.SaveAsync(ct, escrow);
    }

    private string TrackUrl(Escrow e) => options.TrackingBaseUrl.TrimEnd('/') + "/" + e.TrackingToken;

    private async Task<Escrow> LoadForCourierAsync(Guid courierId, Guid escrowId, CancellationToken ct)
    {
        var escrow = await escrows.GetAsync(escrowId, ct) ?? throw new NotFoundException("Job not found.");
        if (escrow.CourierId != courierId) throw new ForbiddenException("This job is assigned to another courier.");
        return escrow;
    }

    private Task<bool> Seen(Guid escrowId, string clientEventId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(clientEventId)) throw new DomainValidationException("clientEventId is required.");
        return orderEvents.ExistsAsync(escrowId, clientEventId, ct);
    }

    private async Task<IReadOnlyList<Upload>> ResolveUploadsAsync(Guid courierId, IReadOnlyList<Guid> ids, CancellationToken ct)
    {
        var distinct = ids.Distinct().ToList();
        var found = await uploads.GetManyAsync(distinct, ct);
        if (found.Count != distinct.Count) throw new DomainValidationException("One of the photos was not found.");
        if (found.Any(u => u.CourierId != courierId)) throw new ForbiddenException("A photo belongs to another courier.");
        if (found.Any(u => !u.Stored)) throw new DomainValidationException("A photo has not finished uploading.");
        return found;
    }

    private async Task<(Party Buyer, Party Seller)> GetPartiesAsync(Escrow e, CancellationToken ct)
    {
        var people = await parties.GetManyAsync([e.BuyerId, e.SellerId], ct);
        return (people.First(p => p.Id == e.BuyerId), people.First(p => p.Id == e.SellerId));
    }
}
