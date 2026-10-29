using CourierEscrow.Application.Helpers;
using CourierEscrow.Application.Interfaces;
using CourierEscrow.Application.Services.Support;
using CourierEscrow.Domain;
using CourierEscrow.Domain.Models;

namespace CourierEscrow.Application.Services;

public sealed record DecideDisputeCommand(DisputeOutcome Outcome, string Reason,
    long BuyerAmount = 0, long SellerAmount = 0, long CourierAmount = 0, long PlatformAmount = 0);

/// <summary>Cases raised by the buyer, seller, courier or ops, and the reviewer's written decision. Never after RELEASED.</summary>
public sealed class DisputeService(
    IEscrowRepository escrows, IPartyRepository parties, ICourierRepository couriers, IDisputeRepository disputes,
    PayoutService payoutService, IAuditLog audit, EscrowCommit commit, Notifier notifier, TimeProvider clock)
{
    public async Task<Dispute> RaiseAsync(Guid escrowId, string raisedBy, string reason, CancellationToken ct = default)
    {
        var escrow = await escrows.GetAsync(escrowId, ct) ?? throw new NotFoundException("Order not found.");
        var now = clock.GetUtcNow();

        var dispute = Dispute.Open(escrow, raisedBy, reason, now); // captures the prior state before the transition
        escrow.OpenDispute(raisedBy, now);
        disputes.Add(dispute);
        audit.Add(AuditEntry.Create(raisedBy, "dispute.open", "order", escrowId.ToString(), reason.Trim(), now));

        foreach (var p in await parties.GetManyAsync([escrow.BuyerId, escrow.SellerId], ct))
            notifier.Queue(escrow, p.Phone, "dispute_opened", SmsTemplates.DisputeOpened(escrow));
        var courier = await couriers.GetAsync(escrow.CourierId, ct);
        if (courier is not null) notifier.Queue(escrow, courier.Phone, "dispute_opened", SmsTemplates.DisputeOpened(escrow));

        await commit.SaveAsync(ct, escrow);
        return dispute;
    }

    public async Task<Dispute> DecideAsync(Guid disputeId, DecideDisputeCommand cmd, string reviewer, CancellationToken ct = default)
    {
        var dispute = await disputes.GetAsync(disputeId, ct) ?? throw new NotFoundException("Dispute not found.");
        var escrow = await escrows.GetAsync(dispute.EscrowId, ct) ?? throw new NotFoundException("Order not found.");
        var now = clock.GetUtcNow();

        var settlement = cmd.Outcome switch
        {
            DisputeOutcome.ReleaseToSeller => Settlement.ForRelease(escrow),
            DisputeOutcome.RefundBuyerKeepDeliveryFee => Settlement.ForRejection(escrow),
            DisputeOutcome.RefundBuyerFull => Settlement.FullRefund(escrow),
            _ => new Settlement(cmd.BuyerAmount, cmd.SellerAmount, cmd.CourierAmount, cmd.PlatformAmount)
        };

        dispute.EnsureCanDecide(cmd.Reason);
        escrow.ResolveDispute(settlement, reviewer, now);   // validates the amounts before anything is recorded
        dispute.Decide(cmd.Outcome, settlement, reviewer, cmd.Reason, now);
        escrow.ClearOpsFlag(reviewer, now);
        await payoutService.StageSettlementAsync(escrow, settlement, "dispute", ct);
        audit.Add(AuditEntry.Create(reviewer, "dispute.decide", "dispute", disputeId.ToString(), $"{cmd.Outcome}: {cmd.Reason.Trim()}", now));

        foreach (var p in await parties.GetManyAsync([escrow.BuyerId, escrow.SellerId], ct))
            notifier.Queue(escrow, p.Phone, "dispute_decided", SmsTemplates.DisputeDecided(escrow, cmd.Outcome.ToString()));

        await commit.SaveAsync(ct, escrow);
        return dispute;
    }

    public Task<IReadOnlyList<Dispute>> ListAsync(DisputeStatus? status, CancellationToken ct = default) => disputes.ListAsync(status, 100, ct);
}
