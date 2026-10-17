namespace CourierEscrow.Domain.Ledger;

public static class Accounts
{
    /// <summary>Money the payment provider holds for us (an asset, so it normally has a net debit balance).</summary>
    public const string ProviderClearing = "provider:clearing";
    public const string ProviderFees = "provider:fees";
    public const string PlatformRevenue = "platform:revenue";

    public static string Escrow(Guid escrowId) => $"escrow:{escrowId}";
    public static string SellerPayable(Guid sellerId) => $"seller:{sellerId}:payable";
    public static string CourierPayable(Guid courierId) => $"courier:{courierId}:payable";
    public static string BuyerRefundable(Guid buyerId) => $"buyer:{buyerId}:refundable";

    public static string PayableFor(Payout p) => p.Kind switch
    {
        PayoutKind.Seller => SellerPayable(p.PayeeId),
        PayoutKind.Courier => CourierPayable(p.PayeeId),
        _ => BuyerRefundable(p.PayeeId)
    };
}

/// <summary>One class of posting rules per money event, so fee and split logic live in one reviewed place.</summary>
public static class EscrowPostings
{
    /// <summary>Buyer paid: money sits at the provider and is owed to the escrow.</summary>
    public static LedgerTransaction Funding(Escrow e, string idempotencyKey, DateTimeOffset now) =>
        LedgerTransaction.Create(e.Id, "funding", idempotencyKey, now,
        [
            new LedgerEntry(Accounts.ProviderClearing, Direction.Debit, e.Total, e.Currency),
            new LedgerEntry(Accounts.Escrow(e.Id), Direction.Credit, e.Total, e.Currency)
        ]);

    /// <summary>The provider charged us for collecting. The platform absorbs it (design decision 6).</summary>
    public static LedgerTransaction CollectionFee(Escrow e, long fee, string idempotencyKey, DateTimeOffset now) =>
        LedgerTransaction.Create(e.Id, "provider_fee", idempotencyKey, now,
        [
            new LedgerEntry(Accounts.ProviderFees, Direction.Debit, fee, e.Currency),
            new LedgerEntry(Accounts.ProviderClearing, Direction.Credit, fee, e.Currency)
        ]);

    /// <summary>Decision made (release, refund or dispute): the escrow is emptied into payables and platform revenue.</summary>
    public static LedgerTransaction Settle(Escrow e, Settlement s, string kind, DateTimeOffset now)
    {
        s.EnsureMatches(e);
        var entries = new List<LedgerEntry> { new(Accounts.Escrow(e.Id), Direction.Debit, e.Total, e.Currency) };
        if (s.Seller > 0) entries.Add(new(Accounts.SellerPayable(e.SellerId), Direction.Credit, s.Seller, e.Currency));
        if (s.Courier > 0) entries.Add(new(Accounts.CourierPayable(e.CourierId), Direction.Credit, s.Courier, e.Currency));
        if (s.BuyerRefund > 0) entries.Add(new(Accounts.BuyerRefundable(e.BuyerId), Direction.Credit, s.BuyerRefund, e.Currency));
        if (s.Platform > 0) entries.Add(new(Accounts.PlatformRevenue, Direction.Credit, s.Platform, e.Currency));
        return LedgerTransaction.Create(e.Id, kind, $"settle:{e.Id}", now, entries);
    }

    /// <summary>A confirmed payout clears the payable and the provider's fee is booked as an expense.</summary>
    public static LedgerTransaction PayoutConfirmed(Payout p, long fee, DateTimeOffset now)
    {
        var entries = new List<LedgerEntry>
        {
            new(Accounts.PayableFor(p), Direction.Debit, p.Amount, p.Currency),
            new(Accounts.ProviderClearing, Direction.Credit, p.Amount + fee, p.Currency)
        };
        if (fee > 0) entries.Add(new(Accounts.ProviderFees, Direction.Debit, fee, p.Currency));
        return LedgerTransaction.Create(p.EscrowId, "payout", $"payout:{p.Id}", now, entries);
    }

    /// <summary>The provider reversed a payout: the money is back at the provider and owed again.</summary>
    public static LedgerTransaction PayoutReversed(Payout p, string eventKey, DateTimeOffset now) =>
        LedgerTransaction.Create(p.EscrowId, "payout_reversal", $"payout-reversal:{eventKey}", now,
        [
            new LedgerEntry(Accounts.ProviderClearing, Direction.Debit, p.Amount, p.Currency),
            new LedgerEntry(Accounts.PayableFor(p), Direction.Credit, p.Amount, p.Currency)
        ]);
}
