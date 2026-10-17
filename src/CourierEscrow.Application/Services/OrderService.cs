using CourierEscrow.Domain;
using CourierEscrow.Domain.Ledger;

namespace CourierEscrow.Application.Services;

public sealed record CreateOrderCommand(
    string BuyerName, string BuyerPhone, string? BuyerNetwork,
    string SellerName, string SellerPhone, string? SellerNetwork,
    Guid CourierId, string ItemDescription, long ItemAmount, long DeliveryFee,
    string? Currency = null, string? PickupAddress = null, string? DeliveryAddress = null,
    IReadOnlyList<Guid>? ListingUploadIds = null);

public sealed record OrderDetail(
    Escrow Escrow, Party Buyer, Party Seller, CourierAccount? Courier,
    IReadOnlyList<OrderEvent> Events, IReadOnlyList<Proof> Proofs, IReadOnlyList<LedgerTransaction> Ledger,
    IReadOnlyList<Payout> Payouts, IReadOnlyList<PaymentIntent> Intents, IReadOnlyList<Dispute> Disputes, Otp? Otp);

public sealed record TrackingStep(string Label, bool Done, bool Current);
public sealed record TrackingPhoto(Guid Id, string Kind);
public sealed record TrackingView(string OrderNumber, string ItemDescription, long Total, string Currency, string State,
    string? SealId, string? CourierName, DateTimeOffset? PickedUpAt, IReadOnlyList<TrackingStep> Steps,
    bool ProductConfirmed, bool CanConfirmProduct, bool CanDeclineProduct,
    IReadOnlyList<TrackingPhoto> Photos, string? PayToAccount, DateTimeOffset? PaymentExpiresAt,
    string TrackingToken, long ItemAmount, long DeliveryFee, string? SellerName, bool ProductDeclined);

public sealed record ProductConfirmResult(Escrow Escrow, bool AlreadyConfirmed);

/// <summary>Creating, cancelling and reading orders. Everything an ops agent does to an order that is not a payment or a handoff step.</summary>
public sealed class OrderService(
    IEscrowRepository escrows, IPartyRepository parties, ICourierRepository couriers, IPaymentIntentRepository intents,
    IPayoutRepository payouts, IProofRepository proofs, IUploadRepository uploads, IOrderEventStore orderEvents, IDisputeRepository disputes,
    IOtpRepository otps, ILedgerStore ledger, INumberSequence sequence, IAuditLog audit, PayoutService payoutService,
    EscrowCommit commit, Notifier notifier, TimeProvider clock, EscrowOptions options)
{
    /// <summary>The ops flag the domain sets when the buyer says the photos are not the item (see <see cref="Escrow.DeclineProduct"/>).</summary>
    public const string DeclinedFlagPrefix = "Buyer says the photos";

    public async Task<Escrow> CreateAsync(CreateOrderCommand cmd, string actor, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var buyerPhone = PhoneNumber.Normalize(cmd.BuyerPhone);
        var sellerPhone = PhoneNumber.Normalize(cmd.SellerPhone);
        if (buyerPhone == sellerPhone) throw new DomainValidationException("Buyer and seller must have different phone numbers.");

        var courier = await couriers.GetAsync(cmd.CourierId, ct) ?? throw new NotFoundException("Courier not found.");
        if (courier.Status != CourierStatus.Active) throw new DomainValidationException("The courier is not active.");

        var listing = await ResolveListingUploadsAsync(cmd.ListingUploadIds, ct);

        var buyer = await FindOrCreateAsync(PartyRole.Buyer, cmd.BuyerName, buyerPhone, cmd.BuyerNetwork, now, ct);
        var seller = await FindOrCreateAsync(PartyRole.Seller, cmd.SellerName, sellerPhone, cmd.SellerNetwork, now, ct);

        var number = $"NX-{await sequence.NextAsync(ct)}";
        var escrow = Escrow.Create(number, buyer.Id, seller.Id, courier.Id, cmd.ItemDescription, cmd.ItemAmount, cmd.DeliveryFee,
            options.PlatformFeeBps, cmd.Currency ?? options.DefaultCurrency, cmd.PickupAddress, cmd.DeliveryAddress, actor, now);

        escrows.Add(escrow);
        foreach (var u in listing) proofs.Add(Proof.Create(escrow, ProofKind.Listing, u, null, null, now));
        audit.Add(AuditEntry.Create(actor, "order.create", "order", escrow.Id.ToString(), number, now));
        notifier.Queue(escrow, buyer.Phone, "order_created_buyer", SmsTemplates.OrderCreatedBuyer(escrow, seller.Name, TrackUrl(escrow)));
        notifier.Queue(escrow, seller.Phone, "order_created_seller", SmsTemplates.OrderCreatedSeller(escrow));
        await commit.SaveAsync(ct, escrow);
        return escrow;
    }

    /// <summary>Before any money moved: payment not started or still pending. The payment intent is closed so a late payment is noticed.</summary>
    public async Task<Escrow> CancelAsync(Guid escrowId, string actor, string reason, CancellationToken ct = default)
    {
        var escrow = await GetRequiredAsync(escrowId, ct);
        var now = clock.GetUtcNow();
        escrow.Cancel(string.IsNullOrWhiteSpace(reason) ? "Cancelled by ops" : reason.Trim(), actor, now);

        foreach (var intent in await intents.ListForEscrowAsync(escrowId, ct))
            if (intent.IsOpen) intent.Expire();

        audit.Add(AuditEntry.Create(actor, "order.cancel", "order", escrowId.ToString(), reason, now));
        await NotifyBothAsync(escrow, SmsTemplates.Cancelled(escrow), "order_cancelled", ct);
        await commit.SaveAsync(ct, escrow);
        return escrow;
    }

    /// <summary>
    /// Ops sends money back to the buyer. Before pickup everything is returned and the courier is not paid;
    /// after a return the item amount goes back and the courier keeps the delivery fee (design decision 3).
    /// </summary>
    public async Task<Escrow> RefundAsync(Guid escrowId, string actor, string reason, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new DomainValidationException("A reason is required.");
        var escrow = await GetRequiredAsync(escrowId, ct);
        var now = clock.GetUtcNow();

        var settlement = escrow.State == EscrowState.Funded ? Settlement.FullRefund(escrow) : Settlement.ForRejection(escrow);
        escrow.Refund(settlement, actor, now);
        escrow.ClearOpsFlag(actor, now);
        await payoutService.StageSettlementAsync(escrow, settlement, "refund", ct);

        audit.Add(AuditEntry.Create(actor, "order.refund", "order", escrowId.ToString(), reason.Trim(), now));
        var buyer = await parties.GetAsync(escrow.BuyerId, ct);
        notifier.Queue(escrow, buyer!.Phone, "refund_buyer", SmsTemplates.RefundBuyer(escrow, settlement));
        if (settlement.Courier > 0)
        {
            var courier = await couriers.GetAsync(escrow.CourierId, ct);
            if (courier is not null) notifier.Queue(escrow, courier.Phone, "refund_courier", SmsTemplates.CourierPaidOnRefund(escrow, settlement));
        }
        await commit.SaveAsync(ct, escrow);
        return escrow;
    }

    public async Task<Escrow> ClearFlagAsync(Guid escrowId, string actor, CancellationToken ct = default)
    {
        var escrow = await GetRequiredAsync(escrowId, ct);
        escrow.ClearOpsFlag(actor, clock.GetUtcNow());
        await commit.SaveAsync(ct, escrow);
        return escrow;
    }

    public Task<Escrow> GetAsync(Guid id, CancellationToken ct = default) => GetRequiredAsync(id, ct);

    public Task<IReadOnlyList<Escrow>> ListAsync(int take = 50, EscrowState? state = null, CancellationToken ct = default) =>
        escrows.ListAsync(Math.Clamp(take, 1, 200), state, ct);

    public Task<IReadOnlyList<Escrow>> ListFlaggedAsync(CancellationToken ct = default) => escrows.ListFlaggedAsync(100, ct);

    public async Task<OrderDetail> GetDetailAsync(Guid id, CancellationToken ct = default)
    {
        var e = await GetRequiredAsync(id, ct);
        var people = await parties.GetManyAsync([e.BuyerId, e.SellerId], ct);
        return new OrderDetail(
            e,
            people.First(p => p.Id == e.BuyerId),
            people.First(p => p.Id == e.SellerId),
            await couriers.GetAsync(e.CourierId, ct),
            await orderEvents.ListForEscrowAsync(id, ct),
            await proofs.ListForEscrowAsync(id, ct),
            await ledger.GetForEscrowAsync(id, ct),
            await payouts.ListForEscrowAsync(id, ct),
            await intents.ListForEscrowAsync(id, ct),
            await disputes.ListForEscrowAsync(id, ct),
            await otps.GetAsync(id, ct));
    }

    public async Task<IReadOnlyDictionary<Guid, Party>> GetPartiesAsync(IEnumerable<Escrow> list, CancellationToken ct = default)
    {
        var ids = list.SelectMany(e => new[] { e.BuyerId, e.SellerId }).Distinct().ToList();
        return (await parties.GetManyAsync(ids, ct)).ToDictionary(p => p.Id);
    }

    public Task<LedgerSummary> GetSummaryAsync(string? currency = null, CancellationToken ct = default) =>
        ledger.GetSummaryAsync((currency ?? options.DefaultCurrency).Trim().ToUpperInvariant(), ct);

    public Task<IReadOnlyList<LedgerTransaction>> GetRecentLedgerAsync(int take = 50, CancellationToken ct = default) =>
        ledger.GetRecentAsync(Math.Clamp(take, 1, 200), ct);

    /// <summary>Read-only status for the buyer's tracking link. Never exposes phone numbers or amounts paid out.</summary>
    public async Task<TrackingView> GetTrackingAsync(string token, CancellationToken ct = default)
    {
        var e = await escrows.GetByTrackingTokenAsync(token, ct) ?? throw new NotFoundException("Tracking link not found.");
        var courier = await couriers.GetAsync(e.CourierId, ct);

        var s = e.State;
        bool Past(params EscrowState[] states) => states.Contains(s);
        var steps = new List<TrackingStep>
        {
            new("Confirm the item photos", e.ProductConfirmedAt is not null, s == EscrowState.Created && e.ProductConfirmedAt is null),
            new("Paid and held", Past(EscrowState.Funded, EscrowState.PickedUp, EscrowState.Delivered, EscrowState.Released, EscrowState.Settled, EscrowState.Rejected, EscrowState.Returned, EscrowState.Refunded, EscrowState.Disputed), s == EscrowState.Funded),
            new(e.SealId is null ? "Picked up" : $"Picked up, seal {e.SealId}", e.PickedUpAt is not null, s == EscrowState.PickedUp),
            new("Courier has arrived", e.DeliveredAt is not null, s == EscrowState.Delivered),
            new("Inspect and share code", Past(EscrowState.Released, EscrowState.Settled), s == EscrowState.Delivered),
            new("Seller paid", s == EscrowState.Settled, s == EscrowState.Released)
        };
        if (s is EscrowState.Rejected or EscrowState.Returned or EscrowState.Refunded or EscrowState.Disputed or EscrowState.Cancelled)
            steps.Add(new(s switch { EscrowState.Cancelled => "Cancelled", EscrowState.Disputed => "In review", EscrowState.Refunded => "Refunded", _ => "Rejected, funds frozen" }, true, true));

        var allProofs = await proofs.ListForEscrowAsync(e.Id, ct);
        var visible = new[] { ProofKind.Listing, ProofKind.Pickup, ProofKind.Arrival };
        var photos = allProofs.Where(p => visible.Contains(p.Kind)).Select(p => new TrackingPhoto(p.Id, p.Kind.ToString())).ToList();

        var intent = (await intents.ListForEscrowAsync(e.Id, ct)).OrderByDescending(i => i.CreatedAt).FirstOrDefault();
        var canAct = s == EscrowState.Created && e.ProductConfirmedAt is null;
        var declined = canAct && e.OpsFlag is not null && e.OpsFlag.StartsWith(DeclinedFlagPrefix, StringComparison.Ordinal);
        var seller = (await parties.GetManyAsync([e.SellerId], ct)).FirstOrDefault();
        return new TrackingView(e.OrderNumber, e.ItemDescription, e.Total, e.Currency, s.ToString(), e.SealId, courier?.Name, e.PickedUpAt, steps,
            e.ProductConfirmedAt is not null, canAct, canAct, photos, intent?.ProviderRef, intent?.ExpiresAt, e.TrackingToken,
            e.ItemAmount, e.DeliveryFee, seller?.Name, declined);
    }

    /// <summary>Buyer confirms the listing photos. Idempotent if they already confirmed.</summary>
    public async Task<ProductConfirmResult> ConfirmProductAsync(string token, CancellationToken ct = default)
    {
        var escrow = await escrows.GetByTrackingTokenAsync(token, ct) ?? throw new NotFoundException("Tracking link not found.");
        var already = escrow.ProductConfirmedAt is not null;
        if (!already) escrow.ConfirmProduct("buyer", clock.GetUtcNow());
        await commit.SaveAsync(ct, escrow);
        return new ProductConfirmResult(escrow, already);
    }

    public async Task DeclineProductAsync(string token, string reason, CancellationToken ct = default)
    {
        var escrow = await escrows.GetByTrackingTokenAsync(token, ct) ?? throw new NotFoundException("Tracking link not found.");
        escrow.DeclineProduct(reason, "buyer", clock.GetUtcNow());
        await commit.SaveAsync(ct, escrow);
    }

    public async Task<Proof> GetTrackingPhotoAsync(string token, Guid proofId, CancellationToken ct = default)
    {
        var e = await escrows.GetByTrackingTokenAsync(token, ct) ?? throw new NotFoundException("Tracking link not found.");
        var proof = (await proofs.ListForEscrowAsync(e.Id, ct)).FirstOrDefault(p => p.Id == proofId)
                    ?? throw new NotFoundException("Photo not found.");
        if (proof.Kind is not (ProofKind.Listing or ProofKind.Pickup or ProofKind.Arrival))
            throw new NotFoundException("Photo not found.");
        return proof;
    }

    private async Task NotifyBothAsync(Escrow e, string body, string template, CancellationToken ct)
    {
        var people = await parties.GetManyAsync([e.BuyerId, e.SellerId], ct);
        foreach (var p in people) notifier.Queue(e, p.Phone, template, body);
    }

    private async Task<Party> FindOrCreateAsync(PartyRole role, string name, string phone, string? network, DateTimeOffset now, CancellationToken ct)
    {
        var existing = await parties.FindAsync(role, phone, ct);
        if (existing is not null) { existing.UpdateWallet(network); return existing; }
        var party = Party.Create(role, name, phone, network, now);
        parties.Add(party);
        return party;
    }

    private string TrackUrl(Escrow e) => options.TrackingBaseUrl.TrimEnd('/') + "/" + e.TrackingToken;

    private async Task<IReadOnlyList<Upload>> ResolveListingUploadsAsync(IReadOnlyList<Guid>? ids, CancellationToken ct)
    {
        var distinct = (ids ?? []).Distinct().ToList();
        if (distinct.Count != Escrow.MinListingPhotos)
            throw new DomainValidationException($"Exactly {Escrow.MinListingPhotos} photos of the item are required so the buyer can confirm what they are buying.");
        var found = await uploads.GetManyAsync(distinct, ct);
        if (found.Count != distinct.Count) throw new DomainValidationException("One of the product photos was not found.");
        if (found.Any(u => !u.Stored)) throw new DomainValidationException("A product photo has not finished uploading.");
        return found;
    }

    private async Task<Escrow> GetRequiredAsync(Guid id, CancellationToken ct) =>
        await escrows.GetAsync(id, ct) ?? throw new NotFoundException($"Order {id} was not found.");
}
