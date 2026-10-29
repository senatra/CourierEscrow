using CourierEscrow.Application.Interfaces;
using CourierEscrow.Application.Services;
using CourierEscrow.Application.Services.Support;
using CourierEscrow.Domain;
using CourierEscrow.Domain.Models;
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
        app.MapGet("/simulate-payment/{token}", SimulationPaymentPage).AllowAnonymous().WithTags("Public");
        app.MapGet("/track/{token}/photos/{proofId:guid}", GetTrackingPhoto).AllowAnonymous().WithTags("Public");
        app.MapPost("/track/{token}/confirm-product", ConfirmProduct).AllowAnonymous().WithTags("Public");
        app.MapPost("/track/{token}/decline-product", DeclineProduct).AllowAnonymous().WithTags("Public");
        app.MapPost("/track/{token}/simulate-payment", SimulatePayment).AllowAnonymous().WithTags("Public");

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
    private static async Task<IResult> GetTracking(string token, OrderService orders, IEscrowRepository escrows,
        IPaymentIntentRepository intents, IPaymentAttemptRepository attempts, SwitchablePaymentProvider provider, CancellationToken ct)
    {
        var view = await orders.GetTrackingAsync(token, ct);
        var escrow = await escrows.GetByTrackingTokenAsync(token, ct);
        var intent = escrow?.PaymentReference is { } reference ? await intents.GetByReferenceAsync(reference, ct) : null;
        var latestAttempt = intent is null ? null : (await attempts.ListForIntentAsync(intent.Id, ct)).LastOrDefault();
        var isMock = provider.Active.Name.Equals("mock", StringComparison.OrdinalIgnoreCase);
        var isOneKhusaSandbox = provider.Active.Name.Equals("onekhusa", StringComparison.OrdinalIgnoreCase)
                                && provider.OneKhusaOptions.Environment.Equals("Sandbox", StringComparison.OrdinalIgnoreCase)
                                && provider.OneKhusaOptions.SandboxConnectorId > 0;
        var pendingMock = intent?.Status == PaymentIntentStatus.Pending
                          && string.Equals(latestAttempt?.Provider, "mock", StringComparison.OrdinalIgnoreCase);
        var pendingOneKhusa = intent?.Status == PaymentIntentStatus.Pending
                              && !string.IsNullOrWhiteSpace(intent.ProviderRef)
                              && string.Equals(latestAttempt?.Provider, "onekhusa", StringComparison.OrdinalIgnoreCase);
        var canSimulate = view.State == EscrowState.PaymentPending.ToString()
                          && ((isMock && pendingMock) || (isOneKhusaSandbox && pendingOneKhusa));
        var simulatorProvider = isOneKhusaSandbox && pendingOneKhusa ? "onekhusa-sandbox" : isMock && pendingMock ? "mock" : null;
        var simulationUnavailableReason = canSimulate ? null
            : provider.Active.Name.Equals("onekhusa", StringComparison.OrdinalIgnoreCase)
                && !provider.OneKhusaOptions.Environment.Equals("Sandbox", StringComparison.OrdinalIgnoreCase)
                ? "Fake payments are only available with OneKhusa Sandbox, never OneKhusa Live."
            : provider.Active.Name.Equals("onekhusa", StringComparison.OrdinalIgnoreCase)
                && provider.OneKhusaOptions.SandboxConnectorId <= 0
                ? "Configure OneKhusa:SandboxConnectorId to use the OneKhusa Sandbox fake-payment action."
                : view.State != EscrowState.PaymentPending.ToString()
                    ? "This order is not currently waiting for payment."
                    : intent?.Status != PaymentIntentStatus.Pending
                        ? "There is no active pending payment to simulate."
                : isMock
                    ? $"This payment was started with {latestAttempt?.Provider ?? "another provider"}. Switching providers does not convert an existing payment; start a new payment after selecting Simulation."
                    : "This payment is not a pending OneKhusa Sandbox request. Only pending Sandbox timed-account payments can use this fake-data action.";
        return Results.Ok(view with
        {
            CanSimulatePayment = canSimulate,
            SimulationUnavailableReason = simulationUnavailableReason,
            SimulatorProvider = simulatorProvider
        });
    }

    private static async Task SimulationPaymentPage(string token, HttpContext http, IWebHostEnvironment env)
    {
        http.Response.ContentType = "text/html; charset=utf-8";
        await http.Response.SendFileAsync(Path.Combine(env.ContentRootPath, "wwwroot", "track", "simulate-payment.html"));
    }

    private static async Task<IResult> SimulatePayment(string token, SimulationPaymentRequest request,
        IEscrowRepository escrows, IPaymentIntentRepository intents, IPaymentAttemptRepository attempts,
        SwitchablePaymentProvider provider, WebhookService webhooks, CollectionService collections, CancellationToken ct)
    {
        var escrow = await escrows.GetByTrackingTokenAsync(token, ct)
                     ?? throw new NotFoundException("Tracking link not found.");
        if (escrow.State != EscrowState.PaymentPending || string.IsNullOrWhiteSpace(escrow.PaymentReference))
            return Results.Conflict(new { detail = "This order is not waiting for a payment." });

        var intent = await intents.GetByReferenceAsync(escrow.PaymentReference, ct);
        var latestAttempt = intent is null ? null : (await attempts.ListForIntentAsync(intent.Id, ct)).LastOrDefault();
        if (intent is null || intent.Status != PaymentIntentStatus.Pending)
            return Results.Conflict(new { detail = "No pending payment request is available to simulate." });

        var status = request.Outcome?.Trim().ToLowerInvariant();
        if (provider.Active is MockPaymentProvider mock)
        {
            if (!string.Equals(latestAttempt?.Provider, "mock", StringComparison.OrdinalIgnoreCase))
                return Results.Conflict(new { detail = "This payment was not created with the Simulation provider." });
            if (status is not ("succeeded" or "failed"))
                return Results.BadRequest(new { detail = "Choose either a successful or failed simulated payment." });

            var body = CollectionBody(intent.Reference, intent.Amount, intent.Currency, status);
            return await PlayWebhook(mock, webhooks, body, ct);
        }

        if (provider.Active.Name.Equals("onekhusa", StringComparison.OrdinalIgnoreCase)
            && provider.OneKhusaOptions.Environment.Equals("Sandbox", StringComparison.OrdinalIgnoreCase)
            && provider.OneKhusaOptions.SandboxConnectorId > 0
            && string.Equals(latestAttempt?.Provider, "onekhusa", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(intent.ProviderRef))
        {
            if (status is not (null or "succeeded"))
                return Results.BadRequest(new { detail = "OneKhusa Sandbox fake data supports successful test payments only." });
            var fakeResult = await provider.OneKhusa.SimulateAcceptRequestToPayAsync(intent.ProviderRef, intent.Amount, ct);
            var notification = new CollectionNotification($"onekhusa-sandbox:{fakeResult.TransactionReference}", intent.Reference,
                CollectionEventKind.Succeeded, intent.Amount, fakeResult.Fee, intent.Currency, fakeResult.TransactionReference);
            var outcome = await collections.HandleAsync(notification, null, ct);
            return Results.Ok(new { simulated = true, outcome = outcome.ToString(), reference = intent.Reference });
        }

        return Results.Conflict(new { detail = "Select OneKhusa Sandbox and configure SandboxConnectorId to simulate this payment." });
    }

    private static async Task<IResult> GetTrackingPhoto(string token, Guid proofId, OrderService orders, IObjectStorage storage, CancellationToken ct)
    {
        var proof = await orders.GetTrackingPhotoAsync(token, proofId, ct);
        var stream = await storage.OpenAsync(proof.ObjectKey, ct)
                     ?? throw new NotFoundException("The photo file is missing.");
        return Results.Stream(stream, "image/jpeg");
    }

    // The buyer approves the photos; only then is the payment requested. Safe to repeat: a second tap resumes a payment
    // request that did not reach the provider the first time.
    private static async Task<IResult> ConfirmProduct(string token, BuyerApprovalService approval, ILoggerFactory loggerFactory, HttpContext httpContext, CancellationToken ct)
    {
        var result = await approval.ApproveAsync(token, ct);
        if (result.Diagnostic is not null)
            loggerFactory.CreateLogger("CourierEscrow.Api.Endpoints.PublicEndpoints")
                .LogError("Payment setup failed for reference {PaymentReference}. TraceId: {TraceId}. Provider diagnostic: {ProviderDiagnostic}",
                    result.Reference, httpContext.TraceIdentifier, result.Diagnostic);
        return Results.Ok(result);
    }

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
        var eventCode = request.Headers[callbackProvider.EventHeader].FirstOrDefault();
        var eventHeaderName = callbackProvider.EventHeader;
        if (string.IsNullOrWhiteSpace(eventCode) && provider.Equals("onekhusa", StringComparison.OrdinalIgnoreCase))
        {
            eventCode = request.Headers["OneKhusa-Webhook-Event"].FirstOrDefault();
            eventHeaderName = "OneKhusa-Webhook-Event";
        }
        var signature = request.Headers[callbackProvider.SignatureHeader].FirstOrDefault();

        var (receipt, itemId) = await webhooks.ReceiveAsync(
            ms.ToArray(),
            signature,
            eventCode,
            ct,
            callbackProvider);

        if (receipt == WebhookReceipt.Unauthorized)
        {
            var eventSecretConfigured = provider.Equals("onekhusa", StringComparison.OrdinalIgnoreCase)
                                        && !string.IsNullOrWhiteSpace(providers.OneKhusaOptions.GetWebhookSecret(eventCode));
            var trimmedSignature = signature?.Trim();
            var signatureFormatValid = !provider.Equals("onekhusa", StringComparison.OrdinalIgnoreCase)
                                       || (trimmedSignature?.Length == 128 && trimmedSignature.All(Uri.IsHexDigit));
            var failureReason = string.IsNullOrWhiteSpace(eventCode) ? "missing_event_code"
                : string.IsNullOrWhiteSpace(signature) ? "missing_signature"
                : provider.Equals("onekhusa", StringComparison.OrdinalIgnoreCase) && !eventSecretConfigured ? "event_secret_missing_or_unmapped"
                : !signatureFormatValid ? "invalid_signature_format"
                : "signature_mismatch";
            log.LogWarning(
                "Webhook rejected with HTTP 401. Provider: {Provider}; FailureReason: {FailureReason}; EventHeader: {EventHeader}; EventCode: {EventCode}; SignatureHeader: {SignatureHeader}; SignaturePresent: {SignaturePresent}; SignatureLength: {SignatureLength}; SignatureFormatValid: {SignatureFormatValid}; EventSecretConfigured: {EventSecretConfigured}; PayloadBytes: {PayloadBytes}; Ip: {Ip}",
                provider, failureReason, eventHeaderName, eventCode, callbackProvider.SignatureHeader,
                !string.IsNullOrWhiteSpace(signature), trimmedSignature?.Length ?? 0, signatureFormatValid,
                eventSecretConfigured, ms.Length,
                request.HttpContext.Connection.RemoteIpAddress);
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

    private static string CollectionBody(string reference, long amount, string currency, string status = "succeeded") =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            eventId = $"sim-pay-{Guid.NewGuid():N}", kind = "collection", reference, status, amount, currency
        });

    private static async Task<IResult> PlayWebhook(MockPaymentProvider mock, WebhookService webhooks, string body, CancellationToken ct)
    {
        var (receipt, id) = await webhooks.ReceiveAsync(System.Text.Encoding.UTF8.GetBytes(body), mock.Sign(body), null, ct);
        return Results.Ok(new { receipt = receipt.ToString(), outcome = id is null ? null : await webhooks.ProcessAsync(id.Value, ct) });
    }
}

public sealed record HandoverCodeRequest(string Code, string? ClientEventId);
public sealed record SimulationPaymentRequest(string? Outcome);
