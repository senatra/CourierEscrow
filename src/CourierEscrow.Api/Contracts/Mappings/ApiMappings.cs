using CourierEscrow.Application.Interfaces;
using CourierEscrow.Application.Services;
using CourierEscrow.Domain;
using CourierEscrow.Domain.Models;
using CourierEscrow.Domain.Ledger;

namespace CourierEscrow.Api;

public static class Mapping
{
    private static string? Mask(string? msisdn) => msisdn is null ? null : PhoneNumber.Mask(msisdn);

    public static OrderSummaryDto ToDto(this Escrow e, Party buyer, Party seller) => new(
        e.Id, e.OrderNumber, e.State.ToString(), e.ItemDescription, e.ItemAmount, e.DeliveryFee, e.PlatformFee, e.Total, e.Currency,
        buyer.Name, Mask(buyer.Phone)!, seller.Name, Mask(seller.Phone)!, e.OpsFlag, e.CreatedAt, e.TrackingToken, e.ProductConfirmedAt);

    public static JobDto ToDto(this CourierJob j, IEnumerable<Proof>? proofs = null) => new(
        j.Escrow.Id, j.Escrow.OrderNumber, j.Escrow.State.ToString(), j.Escrow.ItemDescription, j.Escrow.DeliveryFee, j.Escrow.Currency,
        j.Escrow.PickupAddress, j.Escrow.DeliveryAddress, j.Seller.Name, "+" + j.Seller.Phone, j.Buyer.Name, "+" + j.Buyer.Phone,
        j.Escrow.SealId, j.Escrow.State switch
        {
            EscrowState.Funded => "pickup",
            EscrowState.PickedUp => "arrive",
            EscrowState.Delivered => "otp",
            EscrowState.Rejected => "return",
            _ => "none"
        }, (proofs ?? []).Where(p => p.Kind == ProofKind.Listing).Select(p => p.Id).ToList());

    public static PayoutDto ToDto(this Payout p) => new(
        p.Id, p.EscrowId, p.Kind.ToString(), p.BeneficiaryName, Mask(p.Msisdn)!, p.Network?.ToString(), p.Amount, p.Currency, p.Reference,
        p.Status.ToString(), p.Attempts, p.LastError, p.Fee, p.CreatedAt);

    public static DisputeDto ToDto(this Dispute d) => new(
        d.Id, d.EscrowId, d.RaisedBy, d.Reason, d.Status.ToString(), d.PriorState.ToString(), d.OpenedAt, d.DueAt, d.Outcome?.ToString(),
        d.BuyerAmount, d.SellerAmount, d.CourierAmount, d.PlatformAmount, d.DecidedBy, d.DecisionReason);

    public static LedgerTxDto ToDto(this LedgerTransaction t) => new(
        t.Id, t.EscrowId, t.Kind, t.CreatedAt, t.Entries.Select(x => new LedgerEntryDto(x.Account, x.Direction.ToString(), x.Amount)).ToList());

    public static CourierDto ToDto(this CourierAccount c) => new(c.Id, c.Name, Mask(c.Phone)!, c.WalletNetwork.ToString(), c.Status.ToString(), c.DeviceId is not null);

    public static OrderDetailDto ToDto(this OrderDetail d, long held) => new(
        d.Escrow.ToDto(d.Buyer, d.Seller), d.Courier?.Name, d.Escrow.SealId, d.Escrow.WeightGrams, d.Otp?.Status.ToString(), d.Otp?.Attempts ?? 0,
        d.Events.Select(e => new EventDto(e.Type, e.FromState, e.ToState, e.Actor, e.ClientEventId, e.Payload, e.CreatedAt)).ToList(),
        d.Proofs.Select(p => new ProofDto(p.Id, p.Kind.ToString(), p.Sha256, p.Latitude, p.Longitude, p.ServerTime, p.ClientTime)).ToList(),
        d.Ledger.Select(t => t.ToDto()).ToList(),
        d.Payouts.Select(p => p.ToDto()).ToList(),
        d.Intents.Select(i => new IntentDto(i.Reference, i.Status.ToString(), i.Amount, i.ExpiresAt)).ToList(),
        d.Disputes.Select(x => x.ToDto()).ToList(), held);
}
