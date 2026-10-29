using CourierEscrow.Application.Helpers;
using CourierEscrow.Application.Interfaces;
using CourierEscrow.Domain;
using CourierEscrow.Domain.Models;
using CourierEscrow.Domain.Ledger;
using CourierEscrow.Application.Services.Support;

namespace CourierEscrow.Tests.Fakes;

/// <summary>A table whose writes become visible on commit, with unique constraints enforced the way the database indexes do.</summary>
public sealed class Table<T>(string name)
{
    private readonly List<(string Index, Func<T, string?> Key)> _unique = [];
    public List<T> Rows { get; } = [];
    public List<T> Staged { get; } = [];

    public Table<T> Unique(string index, Func<T, string?> key) { _unique.Add((index, key)); return this; }
    public void Add(T row) => Staged.Add(row);

    public void Check()
    {
        foreach (var (index, key) in _unique)
        {
            var seen = Rows.Select(key).Where(k => k is not null).ToHashSet();
            foreach (var k in Staged.Select(key).Where(k => k is not null))
                if (!seen.Add(k)) throw new DuplicateOperationException($"{name}.{index} '{k}' already exists");
        }
    }

    public void Commit() { Rows.AddRange(Staged); Staged.Clear(); }
    public void Discard() => Staged.Clear();
}

public sealed class MemoryDb : IUnitOfWork, INumberSequence
{
    public Table<Escrow> Escrows { get; } = new Table<Escrow>("escrows").Unique("ref", e => e.PaymentReference).Unique("token", e => e.TrackingToken);
    public Table<Party> Parties { get; } = new Table<Party>("parties").Unique("phone", p => $"{p.Role}:{p.Phone}");
    public Table<StaffUser> Staff { get; } = new Table<StaffUser>("staff_users").Unique("username", u => u.Username);
    public Table<CourierAccount> Couriers { get; } = new Table<CourierAccount>("couriers").Unique("phone", c => c.Phone);
    public Table<PaymentIntent> Intents { get; } = new Table<PaymentIntent>("intents").Unique("ref", i => i.Reference);
    public Table<PaymentAttempt> PaymentAttempts { get; } = new("payment_attempts");
    public Table<Payout> Payouts { get; } = new Table<Payout>("payouts").Unique("ref", p => p.Reference);
    public Table<Otp> Otps { get; } = new Table<Otp>("otps").Unique("escrow", o => o.EscrowId.ToString());
    public Table<Proof> Proofs { get; } = new("proofs");
    public Table<Upload> Uploads { get; } = new("uploads");
    public Table<Dispute> Disputes { get; } = new("disputes");
    public Table<OrderEvent> OrderEvents { get; } = new Table<OrderEvent>("order_events").Unique("client_event", e => e.ClientEventId is null ? null : $"{e.EscrowId}:{e.ClientEventId}");
    public Table<WebhookInboxItem> Inbox { get; } = new Table<WebhookInboxItem>("inbox").Unique("event_key", i => i.EventKey);
    public Table<Notification> Notifications { get; } = new("notifications");
    public Table<AuditEntry> Audit { get; } = new("audit");
    public Table<ReconciliationRun> Runs { get; } = new("runs");
    public Table<ReconciliationItem> RunItems { get; } = new("run_items");
    public Table<LedgerTransaction> Ledger { get; } = new Table<LedgerTransaction>("ledger").Unique("idempotency", t => t.IdempotencyKey);
    public Table<DomainEvent> Outbox { get; } = new("outbox");

    private long _sequence = 1000;
    private readonly List<Action> _commits = [];
    private readonly List<Action> _checks = [];
    private readonly List<Action> _discards = [];

    public int Commits { get; private set; }

    /// <summary>Makes the next commit fail the way a lost optimistic-concurrency race does.</summary>
    public bool ConflictOnNextCommit { get; set; }

    public MemoryDb()
    {
        void Reg<T>(Table<T> t) { _checks.Add(t.Check); _commits.Add(t.Commit); _discards.Add(t.Discard); }
        Reg(Escrows); Reg(Parties); Reg(Staff); Reg(Couriers); Reg(Intents); Reg(PaymentAttempts); Reg(Payouts); Reg(Otps); Reg(Proofs); Reg(Uploads); Reg(Disputes);
        Reg(OrderEvents); Reg(Inbox); Reg(Notifications); Reg(Audit); Reg(Runs); Reg(RunItems); Reg(Ledger); Reg(Outbox);
    }

    public Task CommitAsync(CancellationToken ct)
    {
        try
        {
            if (ConflictOnNextCommit) { ConflictOnNextCommit = false; throw new ConcurrencyConflictException("simulated conflict"); }
            foreach (var check in _checks) check(); // nothing is applied unless every unique constraint holds
            foreach (var commit in _commits) commit();
            Commits++;
        }
        catch { foreach (var d in _discards) d(); throw; }
        return Task.CompletedTask;
    }

    public Task<long> NextAsync(CancellationToken ct) => Task.FromResult(++_sequence);

    public long Balance(string account) => Ledger.Rows.SelectMany(t => t.Entries).Where(e => e.Account == account)
        .Sum(e => e.Direction == Direction.Credit ? e.Amount : -e.Amount);

    public IEnumerable<LedgerEntry> Entries => Ledger.Rows.SelectMany(t => t.Entries);
}

public sealed class MemoryRepos(MemoryDb db) :
    IEscrowRepository, IPartyRepository, ICourierRepository, IStaffUserRepository, IPaymentIntentRepository, IPaymentAttemptRepository, IPayoutRepository, IOtpRepository,
    IProofRepository, IUploadRepository, IDisputeRepository, IOrderEventStore, IWebhookInbox, INotificationStore, IAuditLog,
    IReconciliationStore, ILedgerStore, IOutbox
{
    private static Task<T> R<T>(T v) => Task.FromResult(v);
    private static Task<IReadOnlyList<T>> L<T>(IEnumerable<T> v) => Task.FromResult<IReadOnlyList<T>>(v.ToList());

    // escrows
    Task<Escrow?> IEscrowRepository.GetAsync(Guid id, CancellationToken ct) => R(db.Escrows.Rows.FirstOrDefault(e => e.Id == id));
    public Task<Escrow?> GetByPaymentReferenceAsync(string reference, CancellationToken ct) => R(db.Escrows.Rows.FirstOrDefault(e => e.PaymentReference == reference));
    public Task<Escrow?> GetByTrackingTokenAsync(string token, CancellationToken ct) => R(db.Escrows.Rows.FirstOrDefault(e => e.TrackingToken == token));
    public Task<IReadOnlyList<Escrow>> ListAsync(int take, EscrowState? state, CancellationToken ct) =>
        L(db.Escrows.Rows.Where(e => state is null || e.State == state).OrderByDescending(e => e.CreatedAt).Take(take));
    public Task<IReadOnlyList<Escrow>> ListByStatesAsync(IReadOnlyCollection<EscrowState> states, int take, CancellationToken ct) => L(db.Escrows.Rows.Where(e => states.Contains(e.State)).Take(take));
    public Task<IReadOnlyList<Escrow>> ListForCourierAsync(Guid courierId, CancellationToken ct) =>
        L(db.Escrows.Rows.Where(e => e.CourierId == courierId && e.State is EscrowState.Funded or EscrowState.PickedUp or EscrowState.Delivered or EscrowState.Rejected));
    public Task<IReadOnlyList<Escrow>> ListFlaggedAsync(int take, CancellationToken ct) => L(db.Escrows.Rows.Where(e => e.OpsFlag != null).Take(take));
    public void Add(Escrow escrow) => db.Escrows.Add(escrow);

    // parties
    public Task<Party?> FindAsync(PartyRole role, string phone, CancellationToken ct) => R(db.Parties.Rows.FirstOrDefault(p => p.Role == role && p.Phone == phone));
    Task<Party?> IPartyRepository.GetAsync(Guid id, CancellationToken ct) => R(db.Parties.Rows.FirstOrDefault(p => p.Id == id));
    public Task<IReadOnlyList<Party>> GetManyAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct) => L(db.Parties.Rows.Where(p => ids.Contains(p.Id)));
    public void Add(Party party) => db.Parties.Add(party);

    // couriers
    Task<CourierAccount?> ICourierRepository.GetAsync(Guid id, CancellationToken ct) => R(db.Couriers.Rows.FirstOrDefault(c => c.Id == id));
    public Task<CourierAccount?> FindByPhoneAsync(string phone, CancellationToken ct) => R(db.Couriers.Rows.FirstOrDefault(c => c.Phone == phone));
    Task<IReadOnlyList<CourierAccount>> ICourierRepository.ListAsync(CancellationToken ct) => L(db.Couriers.Rows);
    public void Add(CourierAccount courier) => db.Couriers.Add(courier);

    // staff users
    Task<StaffUser?> IStaffUserRepository.GetAsync(Guid id, CancellationToken ct) => R(db.Staff.Rows.FirstOrDefault(u => u.Id == id));
    public Task<StaffUser?> FindByUsernameAsync(string username, CancellationToken ct) => R(db.Staff.Rows.FirstOrDefault(u => u.Username == username));
    Task<IReadOnlyList<StaffUser>> IStaffUserRepository.ListAsync(CancellationToken ct) => L(db.Staff.Rows);
    public Task<bool> AnyAsync(CancellationToken ct) => R(db.Staff.Rows.Count > 0);
    public Task<int> CountActiveAdminsAsync(CancellationToken ct) => R(db.Staff.Rows.Count(u => u.Role == Roles.OpsAdmin && u.IsActive));
    public void Add(StaffUser user) => db.Staff.Add(user);

    // intents
    public void Add(PaymentIntent intent) => db.Intents.Add(intent);
    public Task<PaymentIntent?> GetByReferenceAsync(string reference, CancellationToken ct) => R(db.Intents.Rows.FirstOrDefault(i => i.Reference == reference));
    Task<IReadOnlyList<PaymentIntent>> IPaymentIntentRepository.ListForEscrowAsync(Guid escrowId, CancellationToken ct) => L(db.Intents.Rows.Where(i => i.EscrowId == escrowId));
    public Task<IReadOnlyList<PaymentIntent>> ListOpenAsync(int take, CancellationToken ct) => L(db.Intents.Rows.Where(i => i.IsOpen).OrderBy(i => i.CreatedAt).Take(take));

    // payment attempts
    public void Add(PaymentAttempt attempt) => db.PaymentAttempts.Add(attempt);
    public Task<IReadOnlyList<PaymentAttempt>> ListForIntentAsync(Guid paymentIntentId, CancellationToken ct) => L(db.PaymentAttempts.Rows.Where(a => a.PaymentIntentId == paymentIntentId).OrderBy(a => a.StartedAt));

    // payouts
    public void Add(Payout payout) => db.Payouts.Add(payout);
    Task<Payout?> IPayoutRepository.GetAsync(Guid id, CancellationToken ct) => R(db.Payouts.Rows.FirstOrDefault(p => p.Id == id));
    Task<Payout?> IPayoutRepository.GetByReferenceAsync(string reference, CancellationToken ct) => R(db.Payouts.Rows.FirstOrDefault(p => p.Reference == reference));
    Task<IReadOnlyList<Payout>> IPayoutRepository.ListForEscrowAsync(Guid escrowId, CancellationToken ct) => L(db.Payouts.Rows.Where(p => p.EscrowId == escrowId));
    Task<IReadOnlyList<Payout>> IPayoutRepository.ListDueAsync(DateTimeOffset now, int take, CancellationToken ct) => L(db.Payouts.Rows.Where(p => p.Status == PayoutStatus.Queued && p.NextAttemptAt <= now).Take(take));
    public Task<IReadOnlyList<Payout>> ListSubmittedBeforeAsync(DateTimeOffset cutoff, int take, CancellationToken ct) => L(db.Payouts.Rows.Where(p => p.Status == PayoutStatus.Submitted && p.SubmittedAt <= cutoff).Take(take));
    Task<IReadOnlyList<Payout>> IPayoutRepository.ListAsync(PayoutStatus? status, int take, CancellationToken ct) => L(db.Payouts.Rows.Where(p => status is null || p.Status == status).Take(take));
    public Task<IReadOnlyList<Payout>> ListSubmittedOnAsync(DateOnly day, CancellationToken ct) =>
        L(db.Payouts.Rows.Where(p => p.SubmittedAt is not null && DateOnly.FromDateTime(p.SubmittedAt.Value.UtcDateTime) == day));

    // otp, proofs, uploads
    Task<Otp?> IOtpRepository.GetAsync(Guid escrowId, CancellationToken ct) => R(db.Otps.Rows.FirstOrDefault(o => o.EscrowId == escrowId));
    public void Add(Otp otp) => db.Otps.Add(otp);
    public void Add(Proof proof) => db.Proofs.Add(proof);
    Task<IReadOnlyList<Proof>> IProofRepository.ListForEscrowAsync(Guid escrowId, CancellationToken ct) => L(db.Proofs.Rows.Where(p => p.EscrowId == escrowId));
    public void Add(Upload upload) => db.Uploads.Add(upload);
    Task<Upload?> IUploadRepository.GetAsync(Guid id, CancellationToken ct) => R(db.Uploads.Rows.FirstOrDefault(u => u.Id == id));
    Task<IReadOnlyList<Upload>> IUploadRepository.GetManyAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct) => L(db.Uploads.Rows.Where(u => ids.Contains(u.Id)));

    // disputes
    public void Add(Dispute dispute) => db.Disputes.Add(dispute);
    Task<Dispute?> IDisputeRepository.GetAsync(Guid id, CancellationToken ct) => R(db.Disputes.Rows.FirstOrDefault(d => d.Id == id));
    Task<IReadOnlyList<Dispute>> IDisputeRepository.ListAsync(DisputeStatus? status, int take, CancellationToken ct) => L(db.Disputes.Rows.Where(d => status is null || d.Status == status).Take(take));
    Task<IReadOnlyList<Dispute>> IDisputeRepository.ListForEscrowAsync(Guid escrowId, CancellationToken ct) => L(db.Disputes.Rows.Where(d => d.EscrowId == escrowId));

    // order events
    public void Add(OrderEvent orderEvent) => db.OrderEvents.Add(orderEvent);
    Task<IReadOnlyList<OrderEvent>> IOrderEventStore.ListForEscrowAsync(Guid escrowId, CancellationToken ct) => L(db.OrderEvents.Rows.Where(e => e.EscrowId == escrowId).OrderBy(e => e.CreatedAt));
    public Task<bool> ExistsAsync(Guid escrowId, string clientEventId, CancellationToken ct) => R(db.OrderEvents.Rows.Any(e => e.EscrowId == escrowId && e.ClientEventId == clientEventId));

    // webhook inbox
    public void Add(WebhookInboxItem item) => db.Inbox.Add(item);
    Task<WebhookInboxItem?> IWebhookInbox.GetAsync(Guid id, CancellationToken ct) => R(db.Inbox.Rows.FirstOrDefault(i => i.Id == id));
    public Task<bool> ExistsAsync(string eventKey, CancellationToken ct) => R(db.Inbox.Rows.Any(i => i.EventKey == eventKey));
    public Task<IReadOnlyList<WebhookInboxItem>> ListPendingAsync(int take, CancellationToken ct) => L(db.Inbox.Rows.Where(i => i.Status == WebhookStatus.Pending).Take(take));

    // notifications, audit, reconciliation
    public void Add(Notification notification) => db.Notifications.Add(notification);
    public Task<IReadOnlyList<Notification>> ListDueAsync(DateTimeOffset now, int take, CancellationToken ct) => L(db.Notifications.Rows.Where(n => n.Status == NotificationStatus.Pending && n.NextAttemptAt <= now).Take(take));
    Task<IReadOnlyList<Notification>> INotificationStore.ListForEscrowAsync(Guid escrowId, CancellationToken ct) => L(db.Notifications.Rows.Where(n => n.EscrowId == escrowId));
    public void Add(AuditEntry entry) => db.Audit.Add(entry);
    Task<IReadOnlyList<AuditEntry>> IAuditLog.ListAsync(int take, CancellationToken ct) => L(db.Audit.Rows.Take(take));
    public void Add(ReconciliationRun run) => db.Runs.Add(run);
    public void Add(ReconciliationItem item) => db.RunItems.Add(item);
    public Task<IReadOnlyList<ReconciliationRun>> ListRunsAsync(int take, CancellationToken ct) => L(db.Runs.Rows.OrderByDescending(r => r.RanAt).Take(take));
    public Task<bool> HasRunForDateAsync(DateOnly day, CancellationToken ct) => Task.FromResult(db.Runs.Rows.Any(r => r.ForDate == day));
    public Task<IReadOnlyList<ReconciliationItem>> ListItemsAsync(Guid runId, CancellationToken ct) => L(db.RunItems.Rows.Where(i => i.RunId == runId));

    // ledger
    public void Add(LedgerTransaction transaction) => db.Ledger.Add(transaction);
    public Task<long> GetBalanceAsync(string account, CancellationToken ct) => R(db.Balance(account));
    public Task<IReadOnlyList<LedgerTransaction>> GetForEscrowAsync(Guid escrowId, CancellationToken ct) => L(db.Ledger.Rows.Where(t => t.EscrowId == escrowId).OrderBy(t => t.CreatedAt));
    public Task<IReadOnlyList<LedgerTransaction>> GetRecentAsync(int take, CancellationToken ct) => L(db.Ledger.Rows.OrderByDescending(t => t.CreatedAt).Take(take));
    public Task<LedgerSummary> GetSummaryAsync(string currency, CancellationToken ct)
    {
        long Net(Func<string, bool> m) => db.Entries.Where(e => e.Currency == currency && m(e.Account)).Sum(e => e.Direction == Direction.Credit ? e.Amount : -e.Amount);
        return R(LedgerSummary.FromNets(currency, Net(a => a == Accounts.ProviderClearing), Net(a => a.StartsWith("escrow:")), Net(a => a.StartsWith("seller:")),
            Net(a => a.StartsWith("courier:")), Net(a => a.StartsWith("buyer:")), Net(a => a == Accounts.PlatformRevenue), Net(a => a == Accounts.ProviderFees)));
    }
    public Task<IReadOnlyDictionary<Guid, long>> GetEscrowBalancesAsync(string currency, CancellationToken ct) =>
        R<IReadOnlyDictionary<Guid, long>>(db.Entries.Where(e => e.Currency == currency && e.Account.StartsWith("escrow:"))
            .GroupBy(e => Guid.Parse(e.Account["escrow:".Length..]))
            .ToDictionary(g => g.Key, g => g.Sum(e => e.Direction == Direction.Credit ? e.Amount : -e.Amount)));

    // outbox
    public void Add(DomainEvent domainEvent) => db.Outbox.Add(domainEvent);
}
