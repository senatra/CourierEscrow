using CourierEscrow.Domain;

namespace CourierEscrow.Application.Services;

public sealed record SimulationResult(string State, string? SealId = null);

public sealed record HandoverPayout(string Kind, string Payee, string Status, long Amount, long Fee, string Currency);

/// <summary>What the hand-over screen shows: where the order is, how many code attempts are left, and what each payee is owed.</summary>
public sealed record HandoverStatus(
    Guid EscrowId, string State, string Currency, long Total, bool CodeIssued, bool CodeUsed, bool CodeLocked, int AttemptsLeft,
    IReadOnlyList<HandoverPayout> Payouts);

/// <summary>The outcome of someone typing a code on the hand-over screen. Wrong codes are a normal result, not an error.</summary>
public sealed record HandoverResult(string Result, int AttemptsLeft, string State, bool Replayed);

/// <summary>
/// Development only (the API maps it behind the dev endpoints). Plays the courier's steps on a funded order so a whole
/// order can be walked through in a browser without a phone. It goes through the same rules and the same commit path as
/// the real courier flow: order events are written, the buyer's code is issued and texted, and "accept" runs the real
/// code check, so the ledger, payouts and SMS all behave exactly as they would for a real hand-over.
/// </summary>
public sealed class DevSimulationService(
    IEscrowRepository escrows, IPartyRepository parties, IOtpRepository otps, IPayoutRepository payouts, EscrowCommit commit, Notifier notifier,
    HandoffService handoff, PayoutService payoutService, TimeProvider clock, EscrowOptions options)
{
    public async Task<SimulationResult> PickupAsync(string token, CancellationToken ct = default)
    {
        var escrow = await LoadAsync(token, ct);
        if (escrow.State != EscrowState.Funded)
            throw new DomainValidationException($"The order must be paid (Funded) before pickup, not {escrow.State}.");

        var now = clock.GetUtcNow();
        var evidence = new PickupEvidence(Escrow.MinPickupPhotos, "SEAL-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant(), 0.5m, new GeoPoint(-13.9626, 33.7741));
        escrow.MarkPickedUp(evidence, "dev-courier", now, "dev-pickup-" + Guid.NewGuid().ToString("N"));

        var code = OtpHasher.NewCode();
        var otp = await otps.GetAsync(escrow.Id, ct);
        if (otp is null) otps.Add(Otp.Issue(escrow.Id, options.OtpSecret, code, now.AddHours(options.OtpLifetimeHours)));
        else otp.Reissue(options.OtpSecret, code, now.AddHours(options.OtpLifetimeHours));

        var buyer = await PartyAsync(escrow.BuyerId, ct);
        var seller = await PartyAsync(escrow.SellerId, ct);
        notifier.Queue(escrow, buyer.Phone, "picked_up_buyer", SmsTemplates.PickedUpBuyer(escrow, code, TrackUrl(escrow)), sensitive: true);
        notifier.Queue(escrow, seller.Phone, "dispatch_proof_seller", SmsTemplates.DispatchProofSeller(escrow, now, evidence.PhotoCount));
        await commit.SaveAsync(ct, escrow);
        return new(escrow.State.ToString(), escrow.SealId);
    }

    public async Task<SimulationResult> ArriveAsync(string token, CancellationToken ct = default)
    {
        var escrow = await LoadAsync(token, ct);
        if (escrow.State != EscrowState.PickedUp)
            throw new DomainValidationException($"The order must be picked up first, not {escrow.State}.");

        escrow.MarkArrived(new ArrivalEvidence(Escrow.MinArrivalPhotos, new GeoPoint(-13.9833, 33.7833)), "dev-courier", clock.GetUtcNow(),
            "dev-arrive-" + Guid.NewGuid().ToString("N"));
        await commit.SaveAsync(ct, escrow);
        return new(escrow.State.ToString());
    }

    /// <summary>The buyer inspects and gives the code. A fresh code is issued and submitted, so the real release path runs.</summary>
    public async Task<SimulationResult> AcceptAsync(string token, CancellationToken ct = default)
    {
        var escrow = await LoadAsync(token, ct);
        if (escrow.State != EscrowState.Delivered)
            throw new DomainValidationException($"The courier must have arrived first, not {escrow.State}.");

        var now = clock.GetUtcNow();
        var code = OtpHasher.NewCode();
        var otp = await otps.GetAsync(escrow.Id, ct);
        if (otp is null) otps.Add(Otp.Issue(escrow.Id, options.OtpSecret, code, now.AddHours(options.OtpLifetimeHours)));
        else otp.Reissue(options.OtpSecret, code, now.AddHours(options.OtpLifetimeHours));

        var result = await handoff.SubmitOtpAsync(escrow.CourierId, escrow.Id, "dev-accept-" + Guid.NewGuid().ToString("N"), code, ct);
        if (result.Result != OtpResult.Accepted) throw new DomainValidationException($"The code was not accepted ({result.Result}).");
        return new(result.State.ToString());
    }

    /// <summary>
    /// The courier types the code the buyer gave them at the door. Nothing is faked: the code is checked against the stored hash,
    /// wrong tries are counted (five lock the order), and a right one releases the money and queues the payouts.
    /// </summary>
    public async Task<HandoverResult> EnterCodeAsync(string token, string code, string clientEventId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(code)) throw new DomainValidationException("Enter the 6-digit code.");
        var escrow = await LoadAsync(token, ct);
        var outcome = await handoff.SubmitOtpAsync(escrow.CourierId, escrow.Id, clientEventId, code, ct);
        return new(outcome.Result.ToString(), outcome.AttemptsLeft, outcome.State.ToString(), outcome.Replayed);
    }

    /// <summary>Plays the provider: submits every due payout for this order, then reports each one as paid. Reaches Settled.</summary>
    public async Task<HandoverStatus> PayOutAsync(string token, CancellationToken ct = default)
    {
        var escrow = await LoadAsync(token, ct);
        if (escrow.State is not (EscrowState.Released or EscrowState.Refunded or EscrowState.Settled))
            throw new DomainValidationException($"Nothing to pay out yet: the order is {escrow.State}. The code must be accepted first.");

        await payoutService.ExecuteDueAsync(ct);
        foreach (var p in (await payouts.ListForEscrowAsync(escrow.Id, ct)).Where(p => p.Status == PayoutStatus.Submitted).ToList())
            await payoutService.ApplyAsync(new PayoutNotification(
                "dev-po-" + Guid.NewGuid().ToString("N"), p.Reference, PayoutEventKind.Succeeded, p.Amount, 0, null, p.ProviderRef), null, ct);

        return await StatusAsync(token, ct);
    }

    public async Task<HandoverStatus> StatusAsync(string token, CancellationToken ct = default)
    {
        var escrow = await LoadAsync(token, ct);
        var otp = await otps.GetAsync(escrow.Id, ct);
        var list = await payouts.ListForEscrowAsync(escrow.Id, ct);
        return new(escrow.Id, escrow.State.ToString(), escrow.Currency, escrow.Total,
            otp is not null, otp?.Status == OtpStatus.Used, otp?.Status == OtpStatus.Locked,
            otp is null ? Otp.MaxAttempts : Math.Max(0, Otp.MaxAttempts - otp.Attempts),
            list.Select(p => new HandoverPayout(p.Kind.ToString(), p.BeneficiaryName, p.Status.ToString(), p.Amount, p.Fee, p.Currency)).ToList());
    }

    private async Task<Escrow> LoadAsync(string token, CancellationToken ct) =>
        await escrows.GetByTrackingTokenAsync(token, ct) ?? throw new NotFoundException("Tracking link not found.");

    private async Task<Party> PartyAsync(Guid id, CancellationToken ct) =>
        await parties.GetAsync(id, ct) ?? throw new NotFoundException("Party not found.");

    private string TrackUrl(Escrow e) => options.TrackingBaseUrl.TrimEnd('/') + "/" + e.TrackingToken;
}
