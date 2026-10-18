using CourierEscrow.Api.Auth;
using CourierEscrow.Api.Contracts.Requests;
using CourierEscrow.Application.Helpers;
using CourierEscrow.Application.Interfaces;
using CourierEscrow.Application.Services;
using CourierEscrow.Application.Services.Support;
using CourierEscrow.Domain.Ledger;
using CourierEscrow.Domain.Models;
using CourierEscrow.Infrastructure.OneKhusa;
using CourierEscrow.Infrastructure.Payments;

namespace CourierEscrow.Api.Endpoints;

public static class AdminEndpoints
{
    //  Route table
    public static void MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/admin").WithTags("Admin").RequireAuthorization(Policies.Staff);

        g.MapGet("/summary", GetSummary);
        g.MapGet("/alerts", GetAlerts);

        // orders
        g.MapGet("/orders", ListOrders);
        g.MapGet("/orders/flagged", ListFlaggedOrders);
        g.MapGet("/orders/{id:guid}", GetOrder);
        g.MapPost("/orders", CreateOrder).RequireAuthorization(Policies.Ops);
        g.MapPost("/orders/{id:guid}/payment", RequestPayment).RequireAuthorization(Policies.Ops);
        g.MapPost("/orders/{id:guid}/cancel", CancelOrder).RequireAuthorization(Policies.Ops);
        g.MapPost("/orders/{id:guid}/refund", RefundOrder).RequireAuthorization(Policies.OpsAdmin);
        g.MapPost("/orders/{id:guid}/otp/reissue", ReissueOtp).RequireAuthorization(Policies.Ops);
        g.MapPost("/orders/{id:guid}/clear-flag", ClearFlag).RequireAuthorization(Policies.Ops);
        g.MapPost("/orders/{id:guid}/dispute", RaiseDispute).RequireAuthorization(Policies.Ops);
        g.MapGet("/orders/{id:guid}/proofs/{proofId:guid}/file", GetProofFile);
        g.MapPost("/uploads/presign", PresignUpload).RequireAuthorization(Policies.Ops);

        // disputes
        g.MapGet("/disputes", ListDisputes);
        g.MapPost("/disputes/{id:guid}/decide", DecideDispute).RequireAuthorization(Policies.OpsAdmin);

        // money
        g.MapGet("/payouts", ListPayouts);
        g.MapPost("/payouts/{id:guid}/retry", RetryPayout).RequireAuthorization(Policies.Finance);
        g.MapGet("/ledger", GetLedger);
        g.MapGet("/reconciliation", ListReconciliationRuns);
        g.MapGet("/reconciliation/{runId:guid}", ListReconciliationItems);
        g.MapPost("/reconciliation/run", RunReconciliation).RequireAuthorization(Policies.Finance);
        g.MapGet("/provider/connectors", ListProviderConnectors).RequireAuthorization(Policies.Finance);
        g.MapGet("/payment-provider", GetPaymentProvider).RequireAuthorization(Policies.Finance);
        g.MapPost("/payment-provider", SetPaymentProvider).RequireAuthorization(Policies.Finance);
        g.MapPost("/payment-provider/sandbox-topup", TopUpSandboxMerchant).RequireAuthorization(Policies.Finance);
        g.MapPost("/orders/{id:guid}/simulate-onekhusa-payment", SimulateOneKhusaPayment).RequireAuthorization(Policies.Finance);
        g.MapGet("/audit", GetAudit).RequireAuthorization(Policies.OpsAdmin);

        // couriers
        g.MapGet("/couriers", ListCouriers);
        g.MapPost("/couriers", CreateCourier).RequireAuthorization(Policies.OpsAdmin);
        g.MapPost("/couriers/{id:guid}/reset-device", ResetCourierDevice).RequireAuthorization(Policies.OpsAdmin);
        g.MapPost("/couriers/{id:guid}/status", SetCourierStatus).RequireAuthorization(Policies.OpsAdmin);
    }

    // Dashboard
    private static async Task<IResult> GetSummary(OrderService orders, AlertsService alerts, CancellationToken ct) =>
        Results.Ok(new SummaryDto(await orders.GetSummaryAsync(null, ct), await alerts.GetAsync(ct)));

    private static async Task<IResult> GetAlerts(AlertsService alerts, CancellationToken ct) =>
        Results.Ok(await alerts.GetAsync(ct));

    // Orders 
    private static async Task<IResult> ListOrders(OrderService orders, int? take, EscrowState? state, CancellationToken ct)
    {
        var list = await orders.ListAsync(take ?? 50, state, ct);
        var people = await orders.GetPartiesAsync(list, ct);
        return Results.Ok(list.Select(e => e.ToDto(people[e.BuyerId], people[e.SellerId])));
    }

    private static async Task<IResult> ListFlaggedOrders(OrderService orders, CancellationToken ct)
    {
        var list = await orders.ListFlaggedAsync(ct);
        var people = await orders.GetPartiesAsync(list, ct);
        return Results.Ok(list.Select(e => e.ToDto(people[e.BuyerId], people[e.SellerId])));
    }

    private static async Task<IResult> GetOrder(Guid id, OrderService orders, CancellationToken ct)
    {
        var detail = await orders.GetDetailAsync(id, ct);
        var held = detail.Ledger
            .SelectMany(t => t.Entries)
            .Where(e => e.Account == Accounts.Escrow(id))
            .Sum(e => e.Direction == Direction.Credit ? e.Amount : -e.Amount);
        return Results.Ok(detail.ToDto(held));
    }

    private static async Task<IResult> CreateOrder(CreateOrderRequest r, HttpContext http, OrderService orders, CancellationToken ct)
    {
        var command = new CreateOrderCommand(
            r.BuyerName, r.BuyerPhone, r.BuyerNetwork,
            r.SellerName, r.SellerPhone, r.SellerNetwork,
            r.CourierId, r.ItemDescription, r.ItemAmount, r.DeliveryFee, r.Currency,
            r.PickupAddress, r.DeliveryAddress, r.ListingUploadIds);

        var e = await orders.CreateAsync(command, http.User.Actor(), ct);
        var people = await orders.GetPartiesAsync([e], ct);
        return Results.Created($"/admin/orders/{e.Id}", e.ToDto(people[e.BuyerId], people[e.SellerId]));
    }

    private static async Task<IResult> PresignUpload(PresignRequest r, HandoffService handoff, CancellationToken ct) =>
        Results.Ok(await handoff.CreateStaffUploadSlotAsync(r.ContentType, ct));

    // Ask the buyer to pay. Safe to repeat: the same reference and idempotency key are reused.
    private static async Task<IResult> RequestPayment(Guid id, HttpContext http, CollectionService collections, CancellationToken ct)
    {
        var start = await collections.StartAsync(id, http.User.Actor(), ct);
        return Results.Ok(new PaymentDto(start.Reference, start.AccountNumber, start.ExpiresAt, start.State));
    }

    private static async Task<IResult> CancelOrder(Guid id, ReasonRequest r, HttpContext http, OrderService orders, CancellationToken ct)
    {
        var e = await orders.CancelAsync(id, http.User.Actor(), r.Reason, ct);
        return Results.Ok(new { state = e.State.ToString() });
    }

    private static async Task<IResult> RefundOrder(Guid id, ReasonRequest r, HttpContext http, OrderService orders, CancellationToken ct)
    {
        var e = await orders.RefundAsync(id, http.User.Actor(), r.Reason, ct);
        return Results.Ok(new { state = e.State.ToString() });
    }

    private static async Task<IResult> ReissueOtp(Guid id, HttpContext http, HandoffService handoff, CancellationToken ct)
    {
        await handoff.ReissueOtpAsync(id, http.User.Actor(), ct);
        return Results.Ok(new { reissued = true });
    }

    private static async Task<IResult> ClearFlag(Guid id, HttpContext http, OrderService orders, CancellationToken ct)
    {
        await orders.ClearFlagAsync(id, http.User.Actor(), ct);
        return Results.Ok(new { cleared = true });
    }

    private static async Task<IResult> RaiseDispute(Guid id, ReasonRequest r, HttpContext http, DisputeService disputes, CancellationToken ct)
    {
        var dispute = await disputes.RaiseAsync(id, http.User.Actor(), r.Reason, ct);
        return Results.Ok(dispute.ToDto());
    }

    // Evidence photo. Hashes were recorded at upload time; the bytes are read straight from storage.
    private static async Task<IResult> GetProofFile(Guid id, Guid proofId, OrderService orders, IObjectStorage storage, CancellationToken ct)
    {
        var detail = await orders.GetDetailAsync(id, ct);
        var proof = detail.Proofs.FirstOrDefault(p => p.Id == proofId)
                    ?? throw new NotFoundException("Proof not found.");
        var stream = await storage.OpenAsync(proof.ObjectKey, ct)
                     ?? throw new NotFoundException("The photo file is missing.");
        return Results.Stream(stream, "image/jpeg");
    }

    // Disputes
    private static async Task<IResult> ListDisputes(DisputeService disputes, DisputeStatus? status, CancellationToken ct) =>
        Results.Ok((await disputes.ListAsync(status, ct)).Select(d => d.ToDto()));

    private static async Task<IResult> DecideDispute(Guid id, DecideRequest r, HttpContext http, DisputeService disputes, CancellationToken ct)
    {
        var command = new DecideDisputeCommand(r.Outcome, r.Reason, r.BuyerAmount, r.SellerAmount, r.CourierAmount, r.PlatformAmount);
        var dispute = await disputes.DecideAsync(id, command, http.User.Actor(), ct);
        return Results.Ok(dispute.ToDto());
    }

    // ───────────────────────── Money ─────────────────────────
    private static async Task<IResult> ListPayouts(PayoutService payouts, PayoutStatus? status, CancellationToken ct) =>
        Results.Ok((await payouts.ListAsync(status, 100, ct)).Select(p => p.ToDto()));

    private static async Task<IResult> RetryPayout(Guid id, RetryPayoutRequest r, HttpContext http, PayoutService payouts, CancellationToken ct)
    {
        var payout = await payouts.RetryAsync(id, r.NewKey, r.Msisdn, r.Network, http.User.Actor(), ct);
        return Results.Ok(payout.ToDto());
    }

    private static async Task<IResult> GetLedger(OrderService orders, int? take, CancellationToken ct) =>
        Results.Ok((await orders.GetRecentLedgerAsync(take ?? 50, ct)).Select(t => t.ToDto()));

    private static async Task<IResult> ListReconciliationRuns(ReconciliationService recon, CancellationToken ct)
    {
        var runs = await recon.ListRunsAsync(30, ct);
        return Results.Ok(runs.Select(ToRunDto));
    }

    private static async Task<IResult> ListReconciliationItems(Guid runId, ReconciliationService recon, CancellationToken ct)
    {
        var items = await recon.ListItemsAsync(runId, ct);
        return Results.Ok(items.Select(ToItemDto));
    }

    private static async Task<IResult> RunReconciliation(ReconciliationService recon, TimeProvider clock, DateOnly? date, CancellationToken ct)
    {
        var day = date ?? DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime.AddDays(-1));
        var result = await recon.RunAsync(day, ct);
        return Results.Ok(new
        {
            run = ToRunDto(result.Run, day),
            items = result.Items.Select(ToItemDto)
        });
    }

    // OneKhusa only: shows connector ids so Airtel and TNM can be set in configuration.
    private static async Task<IResult> ListProviderConnectors(SwitchablePaymentProvider provider, CancellationToken ct) =>
        provider.Active is OneKhusaProvider ok
            ? Results.Ok(await ok.ListConnectorsAsync(ct))
            : Results.Ok(Array.Empty<object>());

    private static IResult GetPaymentProvider(SwitchablePaymentProvider provider) => Results.Ok(new
    {
        selected = provider.Active.Name,
        providers = new object[]
        {
            new { name = "Mock", label = "Simulation", available = true, missingConfiguration = Array.Empty<string>(), environment = (string?)null, webhooksReady = true, currencyCode = (string?)null, sandboxConnectorConfigured = false, payoutConnectorsConfigured = false },
            new
            {
                name = "OneKhusa",
                label = "OneKhusa",
                available = provider.MissingOneKhusaConfiguration.Count == 0,
                missingConfiguration = provider.MissingOneKhusaConfiguration,
                environment = provider.OneKhusaOptions.Environment,
                webhooksReady = provider.OneKhusaOptions.MissingWebhookSecrets.Count == 0,
                missingWebhookSecrets = provider.OneKhusaOptions.MissingWebhookSecrets,
                webhookCallbackUrl = provider.OneKhusaOptions.WebhookCallbackUrl,
                currencyCode = provider.OneKhusaOptions.CurrencyCode,
                sandboxConnectorConfigured = provider.OneKhusaOptions.SandboxConnectorId > 0,
                payoutConnectorsConfigured = provider.OneKhusaOptions.AirtelConnectorId > 0 && provider.OneKhusaOptions.TnmConnectorId > 0
            }
        }
    });

    private static IResult SetPaymentProvider(SetPaymentProviderRequest request, SwitchablePaymentProvider provider)
    {
        try
        {
            provider.Select(request.Provider);
            return GetPaymentProvider(provider);
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { detail = ex.Message, missingConfiguration = provider.MissingOneKhusaConfiguration });
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { detail = ex.Message });
        }
    }

    private static async Task<IResult> TopUpSandboxMerchant(SandboxTopUpRequest request, SwitchablePaymentProvider provider, CancellationToken ct)
    {
        if (provider.Active is not OneKhusaProvider oneKhusa || !provider.OneKhusaOptions.Environment.Equals("Sandbox", StringComparison.OrdinalIgnoreCase))
            return Results.Conflict(new { detail = "Select OneKhusa Sandbox before adding test funds." });
        if (request.Amount < 0.01m || request.Amount > 5_000_000_000m) return Results.BadRequest(new { detail = "Sandbox top-ups must be between 0.01 and 5,000,000,000 MWK." });
        await oneKhusa.TopUpSandboxMerchantAsync(Money.ToMinor(request.Amount), ct);
        return Results.Ok(new { toppedUp = request.Amount, currency = provider.OneKhusaOptions.CurrencyCode });
    }

    private static async Task<IResult> SimulateOneKhusaPayment(Guid id, SwitchablePaymentProvider provider, OrderService orders, CollectionService collections, CancellationToken ct)
    {
        if (provider.Active is not OneKhusaProvider oneKhusa || !provider.OneKhusaOptions.Environment.Equals("Sandbox", StringComparison.OrdinalIgnoreCase))
            return Results.Conflict(new { detail = "Select OneKhusa Sandbox before simulating a payment." });

        var detail = await orders.GetDetailAsync(id, ct);
        var intent = detail.Intents.FirstOrDefault(i => i.Status == PaymentIntentStatus.Pending && !string.IsNullOrWhiteSpace(i.ProviderRef));
        if (intent is null) return Results.Conflict(new { detail = "No pending OneKhusa payment with a timed account number was found." });

        var result = await oneKhusa.SimulateAcceptRequestToPayAsync(intent.ProviderRef!, intent.Amount, ct);
        var notification = new CollectionNotification($"onekhusa-sandbox:{result.TransactionReference}", intent.Reference,
            CollectionEventKind.Succeeded, intent.Amount, result.Fee, intent.Currency, result.TransactionReference);
        var outcome = await collections.HandleAsync(notification, null, ct);
        return Results.Ok(new { simulated = true, outcome = outcome.ToString(), reference = intent.Reference });
    }

    private static async Task<IResult> GetAudit(IAuditLog audit, CancellationToken ct)
    {
        var entries = await audit.ListAsync(100, ct);
        return Results.Ok(entries.Select(a => new { a.Actor, a.Action, a.EntityType, a.EntityId, a.Detail, a.CreatedAt }));
    }

    // Couriers
    private static async Task<IResult> ListCouriers(CourierService couriers, CancellationToken ct) =>
        Results.Ok((await couriers.ListAsync(ct)).Select(c => c.ToDto()));

    private static async Task<IResult> CreateCourier(CreateCourierRequest r, HttpContext http, CourierService couriers, CancellationToken ct)
    {
        var courier = await couriers.CreateAsync(r.Name, r.Phone, r.Network, r.Pin, http.User.Actor(), ct);
        return Results.Created("/admin/couriers", courier.ToDto());
    }

    private static async Task<IResult> ResetCourierDevice(Guid id, HttpContext http, CourierService couriers, CancellationToken ct) =>
        Results.Ok((await couriers.ResetDeviceAsync(id, http.User.Actor(), ct)).ToDto());

    private static async Task<IResult> SetCourierStatus(Guid id, CourierStatusRequest r, HttpContext http, CourierService couriers, CancellationToken ct) =>
        Results.Ok((await couriers.SetStatusAsync(id, r.Active, http.User.Actor(), ct)).ToDto());

    // Mapping helpers
    private static ReconciliationRunDto ToRunDto(ReconciliationRun r) => ToRunDto(r, r.ForDate);

    private static ReconciliationRunDto ToRunDto(ReconciliationRun r, DateOnly forDate) =>
        new(r.Id, forDate.ToString("yyyy-MM-dd"), r.RanAt, r.ProviderCount, r.MatchedCount, r.ExceptionCount);

    private static ReconciliationItemDto ToItemDto(ReconciliationItem i) =>
        new(i.Kind.ToString(), i.Reference, i.Detail);
}
