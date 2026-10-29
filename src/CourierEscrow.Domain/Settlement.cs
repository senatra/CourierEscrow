using CourierEscrow.Domain.Models;

namespace CourierEscrow.Domain;

/// <summary>
/// How the money held for one order is divided. The four parts always add up to the order total.
/// The platform part is revenue; the others become payables that payouts later clear.
/// </summary>
public sealed record Settlement(long BuyerRefund, long Seller, long Courier, long Platform)
{
    public long Total => BuyerRefund + Seller + Courier + Platform;

    /// <summary>Buyer accepted: seller gets the item less the platform fee, courier gets the delivery fee.</summary>
    public static Settlement ForRelease(Escrow e) => new(0, e.SellerProceeds, e.DeliveryFee, e.PlatformFee);

    /// <summary>Rejected and returned: buyer gets the item amount back, courier keeps the delivery fee, no platform fee.</summary>
    public static Settlement ForRejection(Escrow e) => new(e.ItemAmount, 0, e.DeliveryFee, 0);

    /// <summary>Nothing was delivered (for example cancelled after payment but before pickup): everything goes back.</summary>
    public static Settlement FullRefund(Escrow e) => new(e.Total, 0, 0, 0);

    public void EnsureMatches(Escrow e)
    {
        if (BuyerRefund < 0 || Seller < 0 || Courier < 0 || Platform < 0)
            throw new DomainValidationException("Settlement amounts cannot be negative.");
        if (Total != e.Total)
            throw new DomainValidationException($"Settlement adds up to {Total} but the order holds {e.Total}.");
    }
}
