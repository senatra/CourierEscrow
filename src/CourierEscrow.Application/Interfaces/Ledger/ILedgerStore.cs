using CourierEscrow.Domain.Ledger;

namespace CourierEscrow.Application.Interfaces;

public interface ILedgerStore
{
    void Add(LedgerTransaction transaction);
    Task<long> GetBalanceAsync(string account, CancellationToken ct);
    Task<IReadOnlyList<LedgerTransaction>> GetForEscrowAsync(Guid escrowId, CancellationToken ct);
    Task<IReadOnlyList<LedgerTransaction>> GetRecentAsync(int take, CancellationToken ct);
    Task<LedgerSummary> GetSummaryAsync(string currency, CancellationToken ct);
    Task<IReadOnlyDictionary<Guid, long>> GetEscrowBalancesAsync(string currency, CancellationToken ct);
}

public sealed record LedgerSummary(
    string Currency, long ProviderBalance, long HeldInEscrow, long OwedToSellers, long OwedToCouriers,
    long OwedToBuyers, long PlatformRevenue, long ProviderFees)
{
    public bool IsBalanced =>
        ProviderBalance == HeldInEscrow + OwedToSellers + OwedToCouriers + OwedToBuyers + PlatformRevenue - ProviderFees;

    public static LedgerSummary FromNets(string currency, long clearingNet, long escrowNet, long sellerNet,
        long courierNet, long buyerNet, long platformNet, long feesNet) =>
        new(currency, -clearingNet, escrowNet, sellerNet, courierNet, buyerNet, platformNet, -feesNet);
}
