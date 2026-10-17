using CourierEscrow.Api.Auth;
using CourierEscrow.Application;
using CourierEscrow.Application.Services;
using CourierEscrow.Domain;

namespace CourierEscrow.Api.Endpoints;

public static class CourierEndpoints
{
    // ───────────────────────── Route table ─────────────────────────
    public static void MapCourierEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/courier").WithTags("Courier");

        g.MapPost("/login", Login).AllowAnonymous();

        var authed = g.MapGroup("").RequireAuthorization(Policies.Courier);

        authed.MapGet("/jobs", ListJobs);
        authed.MapGet("/jobs/{id:guid}", GetJob);
        authed.MapGet("/jobs/{id:guid}/proofs/{proofId:guid}/file", GetListingPhoto);
        authed.MapPost("/uploads/presign", PresignUpload);
        authed.MapPost("/orders/{id:guid}/events", SubmitEvent);
    }

    // Auth
    private static async Task<IResult> Login(LoginRequest r, CourierService couriers, CancellationToken ct) =>
        Results.Ok(await couriers.LoginAsync(r.Phone, r.Pin, r.DeviceId, ct));

    // Jobs
    private static async Task<IResult> ListJobs(HttpContext http, CourierService couriers, IProofRepository proofs, CancellationToken ct)
    {
        var jobs = await couriers.GetJobsAsync(http.User.CourierId(), ct);
        var dtos = new List<JobDto>();
        foreach (var j in jobs)
            dtos.Add(j.ToDto(await proofs.ListForEscrowAsync(j.Escrow.Id, ct)));
        return Results.Ok(dtos);
    }

    private static async Task<IResult> GetJob(Guid id, HttpContext http, CourierService couriers, IProofRepository proofs, CancellationToken ct)
    {
        var job = await couriers.GetJobAsync(http.User.CourierId(), id, ct);
        return Results.Ok(job.ToDto(await proofs.ListForEscrowAsync(job.Escrow.Id, ct)));
    }

    private static async Task<IResult> GetListingPhoto(
        Guid id, Guid proofId, HttpContext http,
        CourierService couriers, IProofRepository proofs, IObjectStorage storage, CancellationToken ct)
    {
        var job = await couriers.GetJobAsync(http.User.CourierId(), id, ct);
        var proof = (await proofs.ListForEscrowAsync(job.Escrow.Id, ct)).FirstOrDefault(p => p.Id == proofId)
                    ?? throw new NotFoundException("Photo not found.");

        if (proof.Kind != ProofKind.Listing)
            throw new NotFoundException("Photo not found.");

        var stream = await storage.OpenAsync(proof.ObjectKey, ct)
                     ?? throw new NotFoundException("The photo file is missing.");
        return Results.Stream(stream, "image/jpeg");
    }

    private static async Task<IResult> PresignUpload(PresignRequest r, HttpContext http, HandoffService handoff, CancellationToken ct) =>
        Results.Ok(await handoff.CreateUploadSlotAsync(http.User.CourierId(), r.ContentType, ct));

    // Events
    // Every action the courier can take on an order. clientEventId makes a retry from a weak network harmless.
    private static Task<IResult> SubmitEvent(
        Guid id, CourierEventRequest r, HttpContext http,
        HandoffService handoff, CourierService couriers, DisputeService disputes, CancellationToken ct)
    {
        var courier = http.User.CourierId();

        return r.Type.Trim().ToLowerInvariant() switch
        {
            "pickup" => Pickup(courier, id, r, handoff, ct),
            "arrived" => Arrived(courier, id, r, handoff, ct),
            "otp" => SubmitOtp(courier, id, r, handoff, ct),
            "reject" => Reject(courier, id, r, handoff, ct),
            "return" => Return(courier, id, r, handoff, ct),
            "unreachable" => Unreachable(courier, id, r, handoff, ct),
            "dispute" => Dispute(courier, id, r, http, couriers, disputes, ct),
            _ => throw new DomainValidationException(
                    $"Unknown event type '{r.Type}'. Use pickup, arrived, otp, reject, return, unreachable or dispute.")
        };
    }

    private static async Task<IResult> Pickup(Guid courier, Guid id, CourierEventRequest r, HandoffService handoff, CancellationToken ct) =>
        Results.Ok(await handoff.PickupAsync(courier, id,
            new PickupCommand(r.ClientEventId, Photos(r), r.WeightKg ?? 0, r.SealId, Gps(r), r.ClientTime), ct));

    private static async Task<IResult> Arrived(Guid courier, Guid id, CourierEventRequest r, HandoffService handoff, CancellationToken ct) =>
        Results.Ok(await handoff.ArriveAsync(courier, id,
            new ArrivalCommand(r.ClientEventId, Photos(r), Gps(r), r.ClientTime), ct));

    private static async Task<IResult> SubmitOtp(Guid courier, Guid id, CourierEventRequest r, HandoffService handoff, CancellationToken ct)
    {
        var outcome = await handoff.SubmitOtpAsync(courier, id, r.ClientEventId, r.Otp ?? "", ct);
        var body = new { result = outcome.Result.ToString(), outcome.AttemptsLeft, state = outcome.State.ToString(), outcome.Replayed };

        return outcome.Result switch
        {
            OtpResult.Accepted or OtpResult.NotActive when outcome.Replayed || outcome.Result == OtpResult.Accepted => Results.Ok(body),
            OtpResult.Wrong => Results.Json(body, statusCode: 422),
            OtpResult.Locked => Results.Json(body, statusCode: 423),
            OtpResult.Expired => Results.Json(body, statusCode: 410),
            _ => Results.Json(body, statusCode: 409)
        };
    }

    private static async Task<IResult> Reject(Guid courier, Guid id, CourierEventRequest r, HandoffService handoff, CancellationToken ct) =>
        Results.Ok(await handoff.RejectAsync(courier, id,
            new RejectCommand(r.ClientEventId, r.ReasonCode ?? "", Photos(r), Gps(r), r.ClientTime), ct));

    private static async Task<IResult> Return(Guid courier, Guid id, CourierEventRequest r, HandoffService handoff, CancellationToken ct) =>
        Results.Ok(await handoff.ReturnAsync(courier, id,
            new ReturnCommand(r.ClientEventId, Photos(r), r.SellerConfirmed ?? false, Gps(r), r.ClientTime), ct));

    private static async Task<IResult> Unreachable(Guid courier, Guid id, CourierEventRequest r, HandoffService handoff, CancellationToken ct) =>
        Results.Ok(await handoff.FlagUnreachableAsync(courier, id, r.ClientEventId, ct));

    private static async Task<IResult> Dispute(
        Guid courier, Guid id, CourierEventRequest r, HttpContext http,
        CourierService couriers, DisputeService disputes, CancellationToken ct)
    {
        var job = await couriers.GetJobAsync(courier, id, ct); // proves the job is theirs
        var d = await disputes.RaiseAsync(job.Escrow.Id, http.User.Actor(), r.Reason ?? "", ct);
        return Results.Ok(new { disputeId = d.Id, state = EscrowState.Disputed.ToString() });
    }

    // Request helpers
    private static GeoPoint Gps(CourierEventRequest r) => GeoPoint.From(r.Latitude, r.Longitude);

    private static IReadOnlyList<Guid> Photos(CourierEventRequest r) => r.UploadIds ?? [];
}