namespace CourierEscrow.Domain.Ledger;

public enum Direction
{
    Debit = 0,
    Credit = 1
}

public sealed class LedgerEntry
{
    private LedgerEntry() { Account = ""; Currency = ""; } 

    public LedgerEntry(string account, Direction direction, long amount, string currency)
    {
        Id = Guid.NewGuid();
        Account = account;
        Direction = direction;
        Amount = amount;
        Currency = currency;
    }

    public Guid Id { get; private set; }
    public Guid TransactionId { get; private set; }
    public string Account { get; private set; }
    public Direction Direction { get; private set; }

    /// <summary>Always positive, in minor units. The direction carries the sign.</summary>
    public long Amount { get; private set; }
    public string Currency { get; private set; }

    internal void AttachTo(Guid transactionId) => TransactionId = transactionId;
}

/// <summary>
/// An immutable, balanced group of entries (double entry: total debits == total credits).
/// Once persisted it is never updated or deleted; corrections are new, reversing transactions.
/// </summary>
public sealed class LedgerTransaction
{
    private readonly List<LedgerEntry> _entries = [];

    private LedgerTransaction() { Kind = ""; IdempotencyKey = ""; } 

    public Guid Id { get; private set; }
    public Guid EscrowId { get; private set; }
    public string Kind { get; private set; }

    /// <summary>Unique across the ledger. Posting the same business event twice is rejected by the database.</summary>
    public string IdempotencyKey { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public IReadOnlyList<LedgerEntry> Entries => _entries;

    public static LedgerTransaction Create(
        Guid escrowId, string kind, string idempotencyKey, DateTimeOffset now, IEnumerable<LedgerEntry> entries)
    {
        var list = entries.ToList();
        if (list.Count < 2)
            throw new DomainValidationException("A ledger transaction needs at least two entries.");
        if (list.Any(e => e.Amount <= 0))
            throw new DomainValidationException("Ledger entry amounts must be positive.");
        if (list.Select(e => e.Currency).Distinct().Count() != 1)
            throw new DomainValidationException("All entries in a transaction must share one currency.");

        var debits = list.Where(e => e.Direction == Direction.Debit).Sum(e => e.Amount);
        var credits = list.Where(e => e.Direction == Direction.Credit).Sum(e => e.Amount);
        if (debits != credits)
            throw new DomainValidationException($"Unbalanced ledger transaction: debits {debits} != credits {credits}.");

        var tx = new LedgerTransaction
        {
            Id = Guid.NewGuid(),
            EscrowId = escrowId,
            Kind = kind,
            IdempotencyKey = idempotencyKey,
            CreatedAt = now
        };
        foreach (var entry in list)
        {
            entry.AttachTo(tx.Id);
            tx._entries.Add(entry);
        }
        return tx;
    }
}
