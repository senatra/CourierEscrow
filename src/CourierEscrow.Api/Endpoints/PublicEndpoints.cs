using CourierEscrow.Application.Interfaces;
using CourierEscrow.Application.Services;
using CourierEscrow.Application.Services.Support;
using CourierEscrow.Domain;
using CourierEscrow.Infrastructure.Payments;

namespace CourierEscrow.Api.Endpoints;

public static class PublicEndpoints
{
    // Route table
    public static void MapPublicEndpoints(this IEndpointRouteBuilder app, bool development)
    {
        app.MapGet("/health", Health).AllowAnonymous();

        // Buyer tracking
        app.MapGet("/t/{token}", TrackingPage).AllowAnonymous().WithTags("Public");
        app.MapGet("/track/{token}", GetTracking).AllowAnonymous().WithTags("Public");
        app.MapGet("/track/{token}/photos/{proofId:guid}", GetTrackingPhoto).AllowAnonymous().WithTags("Public");
        app.MapPost("/track/{token}/confirm-product", ConfirmProduct).AllowAnonymous().WithTags("Public");
        app.MapPost("/track/{token}/decline-product", DeclineProduct).AllowAnonymous().WithTags("Public");

        // Photo upload target from POST /courier/uploads/presign: signed and expiring, so no login is needed.
        app.MapPut("/uploads/{id:guid}", StoreUpload).AllowAnonymous().WithTags("Public").DisableAntiforgery();

        // Provider callbacks
        app.MapPost("/webhooks/{provider}", ReceiveWebhook).AllowAnonymous().WithTags("Webhooks");

        if (development)
            MapDevEndpoints(app);
    }

    // Development helpers: play the provider's webhooks against the mock provider. Not mapped in production.
    private static void MapDevEndpoints(IEndpointRouteBuilder app)
    {
        var dev = app.MapGroup("/dev").WithTags("Development").AllowAnonymous();

        dev.MapGet("/ping", DevPing);
        dev.MapGet("/sms", DevSms);
        dev.MapPost("/pay/{escrowId:guid}", DevPay);
        dev.MapPost("/pay-by-token/{token}", DevPayByToken);
        dev.MapPost("/payout-success/{reference}", DevPayoutSuccess);
        dev.MapPost("/approve/{escrowId:guid}", DevApprove);
        dev.MapPost("/pickup/{token}", DevPickup);
        dev.MapPost("/deliver/{token}", DevDeliver);
        dev.MapPost("/accept/{token}", DevAccept);

        // The courier's hand-over screen: the courier types the code the buyer gives them and the real check runs.
        dev.MapGet("/handover", DevHandoverPage);
        dev.MapGet("/handover/{token}", DevHandoverStatus);
        dev.MapPost("/handover/{token}/code", DevHandoverCode);
        dev.MapPost("/handover/{token}/payouts", DevHandoverPayouts);
    }

    // Health
    private static IResult Health() => Results.Ok(new { status = "ok" });

    // Buyer tracking
    private static async Task TrackingPage(HttpContext http, IWebHostEnvironment env)
    {
        var file = Path.Combine(env.ContentRootPath, "wwwroot", "track", "index.html");
        http.Response.ContentType = "text/html; charset=utf-8";
        await http.Response.SendFileAsync(file);
    }

    // Buyer's tracking link. The unguessable token is the only credential.
    private static async Task<IResult> GetTracking(string token, OrderService orders, CancellationToken ct) =>
        Results.Ok(await orders.GetTrackingAsync(token, ct));

    private static async Task<IResult> GetTrackingPhoto(string token, Guid proofId, OrderService orders, IObjectStorage storage, CancellationToken ct)
    {
        var proof = await orders.GetTrackingPhotoAsync(token, proofId, ct);
        var stream = await storage.OpenAsync(proof.ObjectKey, ct)
                     ?? throw new NotFoundException("The photo file is missing.");
        return Results.Stream(stream, "image/jpeg");
    }

    // The buyer approves the photos; only then is the payment requested. Safe to repeat: a second tap resumes a payment
    // request that did not reach the provider the first time.
    private static async Task<IResult> ConfirmProduct(string token, BuyerApprovalService approval, CancellationToken ct) =>
        Results.Ok(await approval.ApproveAsync(token, ct));

    private static async Task<IResult> DeclineProduct(string token, ReasonRequest r, OrderService orders, CancellationToken ct)
    {
        await orders.DeclineProductAsync(token, r.Reason, ct);
        return Results.Ok(new { declined = true });
    }

    // Uploads
    private static async Task<IResult> StoreUpload(Guid id, long exp, string? sig, HttpRequest request, HandoffService handoff, CancellationToken ct)
    {
        await handoff.StoreUploadAsync(id, exp, sig, request.Body, ct);
        return Results.NoContent();
    }

    //  Webhooks
    // Verify the signature, keep the raw body, answer quickly, then apply it.
    private static async Task<IResult> ReceiveWebhook(
        string provider, HttpRequest request, SwitchablePaymentProvider providers, WebhookService webhooks,
        ILogger<WebhookService> log, CancellationToken ct)
    {
        var callbackProvider = providers.GetProvider(provider);
        if (callbackProvider is null)
            return Results.NotFound();

        using var ms = new MemoryStream();
        await request.Body.CopyToAsync(ms, ct);

        var (receipt, itemId) = await webhooks.ReceiveAsync(
            ms.ToArray(),
            request.Headers[callbackProvider.SignatureHeader].FirstOrDefault(),
            request.Headers[callbackProvider.EventHeader].FirstOrDefault(),
            ct,
            callbackProvider);

        if (receipt == WebhookReceipt.Unauthorized)
        {
            log.LogWarning("Webhook with a bad signature from {Ip}", request.HttpContext.Connection.RemoteIpAddress);
            return Results.Unauthorized();
        }

        // Apply it now for a fast result. If that fails the item stays in the inbox and the worker retries it.
        if (receipt == WebhookReceipt.Stored)
        {
            try { await webhooks.ProcessAsync(itemId!.Value, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning(ex, "Webhook {Id} will be retried by the worker.", itemId);
            }
        }

        return Results.Ok(new { received = receipt.ToString() });
    }

    // Development
    private static IResult DevPing() => Results.Ok(new { dev = true });

    // The SMS the system "sent" (buyer code, tracking link), so nobody has to dig through the log.
    private static IResult DevSms(Guid? escrowId, ISmsCapture sms) =>
        Results.Ok(sms.Recent(200)
            .Where(x => escrowId is null || x.EscrowId == escrowId)
            .Select(x => new { x.At, x.ToPhone, x.Template, x.Body }));

    private static async Task<IResult> DevPay(Guid escrowId, long? amount, OrderService orders, IPaymentProvider provider, WebhookService webhooks, CancellationToken ct)
    {
        var mock = RequireMock(provider);
        var e = await orders.GetAsync(escrowId, ct);
        if (e.PaymentReference is null) throw new DomainValidationException("Start the payment first.");

        var body = CollectionBody(e.PaymentReference, amount ?? e.Total, e.Currency);
        return await PlayWebhook(mock, webhooks, body, ct);
    }

    // Same as /dev/pay, but keyed by the buyer's tracking token so the buyer page can offer a "Simulate payment" button.
    private static async Task<IResult> DevPayByToken(string token, IEscrowRepository escrows, IPaymentProvider provider, WebhookService webhooks, CancellationToken ct)
    {
        var mock = RequireMock(provider);
        var e = await escrows.GetByTrackingTokenAsync(token, ct) ?? throw new NotFoundException("Tracking link not found.");
        if (e.PaymentReference is null) throw new DomainValidationException("Start the payment first.");

        var body = CollectionBody(e.PaymentReference, e.Total, e.Currency);
        return await PlayWebhook(mock, webhooks, body, ct);
    }

    private static async Task<IResult> DevPayoutSuccess(string reference, long amount, IPaymentProvider provider, WebhookService webhooks, CancellationToken ct)
    {
        var mock = RequireMock(provider);
        var body = $"{{\"eventId\":\"dev-po-{Guid.NewGuid():N}\",\"kind\":\"payout\",\"reference\":\"{reference}\",\"status\":\"succeeded\",\"amount\":{amount},\"fee\":10000}}";
        return await PlayWebhook(mock, webhooks, body, ct);
    }

    // Plays the buyer tapping "Yes, this is the item" on the tracking page: confirms the photos and starts the payment request.
    private static async Task<IResult> DevApprove(Guid escrowId, OrderService orders, BuyerApprovalService approval, CancellationToken ct)
    {
        var e = await orders.GetAsync(escrowId, ct);
        return Results.Ok(await approval.ApproveAsync(e.TrackingToken, ct));
    }

    // The courier's steps, played through the real rules and commit path (events, buyer code SMS, ledger, payouts).
    private static async Task<IResult> DevPickup(string token, DevSimulationService sim, CancellationToken ct)
    {
        var r = await sim.PickupAsync(token, ct);
        return Results.Ok(new { state = r.State, seal = r.SealId });
    }

    private static async Task<IResult> DevDeliver(string token, DevSimulationService sim, CancellationToken ct) =>
        Results.Ok(new { state = (await sim.ArriveAsync(token, ct)).State });

    private static async Task<IResult> DevAccept(string token, DevSimulationService sim, CancellationToken ct) =>
        Results.Ok(new { state = (await sim.AcceptAsync(token, ct)).State });

    private static async Task DevHandoverPage(HttpContext http, IWebHostEnvironment env)
    {
        http.Response.ContentType = "text/html; charset=utf-8";
        await http.Response.SendFileAsync(Path.Combine(env.ContentRootPath, "wwwroot", "dev", "handover.html"));
    }

    private static async Task<IResult> DevHandoverStatus(string token, DevSimulationService sim, CancellationToken ct) =>
        Results.Ok(await sim.StatusAsync(token, ct));

    // A wrong code is a normal answer (200 with result = Wrong and the attempts left), so the screen can show it without
    // treating it as a failure. Each press of the button is a new client event; a double-tap sends the same id and is not counted twice.
    private static async Task<IResult> DevHandoverCode(string token, HandoverCodeRequest r, DevSimulationService sim, CancellationToken ct) =>
        Results.Ok(await sim.EnterCodeAsync(token, r.Code, string.IsNullOrWhiteSpace(r.ClientEventId) ? "dev-code-" + Guid.NewGuid().ToString("N") : r.ClientEventId, ct));

    private static async Task<IResult> DevHandoverPayouts(string token, DevSimulationService sim, CancellationToken ct) =>
        Results.Ok(await sim.PayOutAsync(token, ct));

    // ───────────────────────── Dev helpers ─────────────────────────
    private static MockPaymentProvider RequireMock(IPaymentProvider provider) =>
        provider switch
        {
            MockPaymentProvider mock => mock,
            SwitchablePaymentProvider { Active: MockPaymentProvider mock } => mock,
            _ => throw new DomainValidationException("Only available with the simulation provider.")
        };

    private static string CollectionBody(string reference, long amount, string currency) =>
        $"{{\"eventId\":\"dev-pay-{Guid.NewGuid():N}\",\"kind\":\"collection\",\"reference\":\"{reference}\",\"status\":\"succeeded\",\"amount\":{amount},\"currency\":\"{currency}\"}}";

    private static async Task<IResult> PlayWebhook(MockPaymentProvider mock, WebhookService webhooks, string body, CancellationToken ct)
    {
        var (receipt, id) = await webhooks.ReceiveAsync(System.Text.Encoding.UTF8.GetBytes(body), mock.Sign(body), null, ct);
        return Results.Ok(new { receipt = receipt.ToString(), outcome = id is null ? null : await webhooks.ProcessAsync(id.Value, ct) });
    }
}

public sealed record HandoverCodeRequest(string Code, string? ClientEventId);
