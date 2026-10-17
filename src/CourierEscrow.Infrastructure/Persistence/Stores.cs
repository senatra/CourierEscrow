using System.Linq.Expressions;
using System.Text.Json;
using CourierEscrow.Application;
using CourierEscrow.Domain;
using CourierEscrow.Domain.Ledger;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CourierEscrow.Infrastructure.Persistence;

// Reads that feed a later change are TRACKED on purpose: EF then hands back the same instance everywhere in one
// request, so "are all payouts confirmed?" sees the payout that was confirmed a moment ago and not its saved copy.

public sealed class EscrowRepository(EscrowDbContext db) : IEscrowRepository
{
    private static readonly EscrowState[] CourierStates = [EscrowState.Funded, EscrowState.PickedUp, EscrowState.Delivered, EscrowState.Rejected];

    public Task<Escrow?> GetAsync(Guid id, CancellationToken ct) => db.Escrows.FirstOrDefaultAsync(e => e.Id == id, ct);
    public Task<Escrow?> GetByPaymentReferenceAsync(string reference, CancellationToken ct) => db.Escrows.FirstOrDefaultAsync(e => e.PaymentReference == reference, ct);
    // TRACKED, not AsNoTracking: the buyer's approval, decline and the dev steps all change the order they load by token,
    // and an untracked copy is silently never saved (the approval vanished and payment then failed with "must confirm").
    public Task<Escrow?> GetByTrackingTokenAsync(string token, CancellationToken ct) => db.Escrows.FirstOrDefaultAsync(e => e.TrackingToken == token, ct);

    public async Task<IReadOnlyList<Escrow>> ListAsync(int take, EscrowState? state, CancellationToken ct) =>
        await db.Escrows.AsNoTracking().Where(e => state == null || e.State == state).OrderByDescending(e => e.CreatedAt).Take(take).ToListAsync(ct);

    public async Task<IReadOnlyList<Escrow>> ListByStatesAsync(IReadOnlyCollection<EscrowState> states, int take, CancellationToken ct)
    {
        var list = states.ToArray();
        return await db.Escrows.AsNoTracking().Where(e => list.Contains(e.State)).OrderBy(e => e.CreatedAt).Take(take).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<Escrow>> ListForCourierAsync(Guid courierId, CancellationToken ct) =>
        await db.Escrows.AsNoTracking().Where(e => e.CourierId == courierId && CourierStates.Contains(e.State)).OrderBy(e => e.CreatedAt).ToListAsync(ct);

    public async Task<IReadOnlyList<Escrow>> ListFlaggedAsync(int take, CancellationToken ct) =>
        await db.Escrows.AsNoTracking().Where(e => e.OpsFlag != null).OrderByDescending(e => e.CreatedAt).Take(take).ToListAsync(ct);

    public void Add(Escrow escrow) => db.Escrows.Add(escrow);
}

public sealed class PartyRepository(EscrowDbContext db) : IPartyRepository
{
    public Task<Party?> FindAsync(PartyRole role, string phone, CancellationToken ct) => db.Parties.FirstOrDefaultAsync(p => p.Role == role && p.Phone == phone, ct);
    public Task<Party?> GetAsync(Guid id, CancellationToken ct) => db.Parties.FirstOrDefaultAsync(p => p.Id == id, ct);
    public async Task<IReadOnlyList<Party>> GetManyAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        var list = ids.ToArray();
        return await db.Parties.Where(p => list.Contains(p.Id)).ToListAsync(ct);
    }
    public void Add(Party party) => db.Parties.Add(party);
}

public sealed class CourierRepository(EscrowDbContext db) : ICourierRepository
{
    public Task<CourierAccount?> GetAsync(Guid id, CancellationToken ct) => db.Couriers.FirstOrDefaultAsync(c => c.Id == id, ct);
    public Task<CourierAccount?> FindByPhoneAsync(string phone, CancellationToken ct) => db.Couriers.FirstOrDefaultAsync(c => c.Phone == phone, ct);
    public async Task<IReadOnlyList<CourierAccount>> ListAsync(CancellationToken ct) => await db.Couriers.AsNoTracking().OrderBy(c => c.Name).ToListAsync(ct);
    public void Add(CourierAccount courier) => db.Couriers.Add(courier);
}

public sealed class StaffUserRepository(EscrowDbContext db) : IStaffUserRepository
{
    public Task<StaffUser?> GetAsync(Guid id, CancellationToken ct) => db.StaffUsers.FirstOrDefaultAsync(u => u.Id == id, ct);
    public Task<StaffUser?> FindByUsernameAsync(string username, CancellationToken ct) => db.StaffUsers.FirstOrDefaultAsync(u => u.Username == username, ct);
    public async Task<IReadOnlyList<StaffUser>> ListAsync(CancellationToken ct) => await db.StaffUsers.AsNoTracking().OrderBy(u => u.DisplayName).ToListAsync(ct);
    public Task<bool> AnyAsync(CancellationToken ct) => db.StaffUsers.AnyAsync(ct);
    public Task<int> CountActiveAdminsAsync(CancellationToken ct) => db.StaffUsers.CountAsync(u => u.Role == Roles.OpsAdmin && u.Status == StaffStatus.Active, ct);
    public void Add(StaffUser user) => db.StaffUsers.Add(user);
}

public sealed class PaymentIntentRepository(EscrowDbContext db) : IPaymentIntentRepository
{
    public void Add(PaymentIntent intent) => db.PaymentIntents.Add(intent);
    public Task<PaymentIntent?> GetByReferenceAsync(string reference, CancellationToken ct) => db.PaymentIntents.FirstOrDefaultAsync(i => i.Reference == reference, ct);
    public async Task<IReadOnlyList<PaymentIntent>> ListForEscrowAsync(Guid escrowId, CancellationToken ct) =>
        await db.PaymentIntents.Where(i => i.EscrowId == escrowId).OrderBy(i => i.CreatedAt).ToListAsync(ct);
    public async Task<IReadOnlyList<PaymentIntent>> ListOpenAsync(int take, CancellationToken ct) =>
        await db.PaymentIntents.Where(i => i.Status == PaymentIntentStatus.Initiated || i.Status == PaymentIntentStatus.Pending).OrderBy(i => i.CreatedAt).Take(take).ToListAsync(ct);
}

public sealed class PayoutRepository(EscrowDbContext db) : IPayoutRepository
{
    public void Add(Payout payout) => db.Payouts.Add(payout);
    public Task<Payout?> GetAsync(Guid id, CancellationToken ct) => db.Payouts.FirstOrDefaultAsync(p => p.Id == id, ct);
    public Task<Payout?> GetByReferenceAsync(string reference, CancellationToken ct) => db.Payouts.FirstOrDefaultAsync(p => p.Reference == reference, ct);
    public async Task<IReadOnlyList<Payout>> ListForEscrowAsync(Guid escrowId, CancellationToken ct) =>
        await db.Payouts.Where(p => p.EscrowId == escrowId).OrderBy(p => p.CreatedAt).ToListAsync(ct);
    public async Task<IReadOnlyList<Payout>> ListDueAsync(DateTimeOffset now, int take, CancellationToken ct) =>
        await db.Payouts.Where(p => p.Status == PayoutStatus.Queued && p.NextAttemptAt <= now).OrderBy(p => p.NextAttemptAt).Take(take).ToListAsync(ct);
    public async Task<IReadOnlyList<Payout>> ListSubmittedBeforeAsync(DateTimeOffset cutoff, int take, CancellationToken ct) =>
        await db.Payouts.Where(p => p.Status == PayoutStatus.Submitted && p.SubmittedAt <= cutoff).OrderBy(p => p.SubmittedAt).Take(take).ToListAsync(ct);
    public async Task<IReadOnlyList<Payout>> ListAsync(PayoutStatus? status, int take, CancellationToken ct) =>
        await db.Payouts.AsNoTracking().Where(p => status == null || p.Status == status).OrderByDescending(p => p.CreatedAt).Take(take).ToListAsync(ct);
    public async Task<IReadOnlyList<Payout>> ListSubmittedOnAsync(DateOnly day, CancellationToken ct)
    {
        var from = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var to = from.AddDays(1);
        return await db.Payouts.AsNoTracking().Where(p => p.SubmittedAt >= from && p.SubmittedAt < to).ToListAsync(ct);
    }
}

public sealed class OtpRepository(EscrowDbContext db) : IOtpRepository
{
    public Task<Otp?> GetAsync(Guid escrowId, CancellationToken ct) => db.Otps.FirstOrDefaultAsync(o => o.EscrowId == escrowId, ct);
    public void Add(Otp otp) => db.Otps.Add(otp);
}

public sealed class ProofRepository(EscrowDbContext db) : IProofRepository
{
    public void Add(Proof proof) => db.Proofs.Add(proof);
    public async Task<IReadOnlyList<Proof>> ListForEscrowAsync(Guid escrowId, CancellationToken ct) =>
        await db.Proofs.AsNoTracking().Where(p => p.EscrowId == escrowId).OrderBy(p => p.ServerTime).ToListAsync(ct);
}

public sealed class UploadRepository(EscrowDbContext db) : IUploadRepository
{
    public void Add(Upload upload) => db.Uploads.Add(upload);
    public Task<Upload?> GetAsync(Guid id, CancellationToken ct) => db.Uploads.FirstOrDefaultAsync(u => u.Id == id, ct);
    public async Task<IReadOnlyList<Upload>> GetManyAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        var list = ids.ToArray();
        return await db.Uploads.Where(u => list.Contains(u.Id)).ToListAsync(ct);
    }
}

public sealed class DisputeRepository(EscrowDbContext db) : IDisputeRepository
{
    public void Add(Dispute dispute) => db.Disputes.Add(dispute);
    public Task<Dispute?> GetAsync(Guid id, CancellationToken ct) => db.Disputes.FirstOrDefaultAsync(d => d.Id == id, ct);
    public async Task<IReadOnlyList<Dispute>> ListAsync(DisputeStatus? status, int take, CancellationToken ct) =>
        await db.Disputes.AsNoTracking().Where(d => status == null || d.Status == status).OrderBy(d => d.DueAt).Take(take).ToListAsync(ct);
    public async Task<IReadOnlyList<Dispute>> ListForEscrowAsync(Guid escrowId, CancellationToken ct) =>
        await db.Disputes.AsNoTracking().Where(d => d.EscrowId == escrowId).OrderBy(d => d.OpenedAt).ToListAsync(ct);
}

public sealed class OrderEventStore(EscrowDbContext db) : IOrderEventStore
{
    public void Add(OrderEvent orderEvent) => db.OrderEvents.Add(orderEvent);
    public async Task<IReadOnlyList<OrderEvent>> ListForEscrowAsync(Guid escrowId, CancellationToken ct) =>
        await db.OrderEvents.AsNoTracking().Where(e => e.EscrowId == escrowId).OrderBy(e => e.CreatedAt).ToListAsync(ct);
    public Task<bool> ExistsAsync(Guid escrowId, string clientEventId, CancellationToken ct) =>
        db.OrderEvents.AnyAsync(e => e.EscrowId == escrowId && e.ClientEventId == clientEventId, ct);
}

public sealed class WebhookInbox(EscrowDbContext db) : IWebhookInbox
{
    public void Add(WebhookInboxItem item) => db.WebhookInbox.Add(item);
    public Task<WebhookInboxItem?> GetAsync(Guid id, CancellationToken ct) => db.WebhookInbox.FirstOrDefaultAsync(i => i.Id == id, ct);
    public Task<bool> ExistsAsync(string eventKey, CancellationToken ct) => db.WebhookInbox.AnyAsync(i => i.EventKey == eventKey, ct);
    public async Task<IReadOnlyList<WebhookInboxItem>> ListPendingAsync(int take, CancellationToken ct) =>
        await db.WebhookInbox.AsNoTracking().Where(i => i.Status == WebhookStatus.Pending).OrderBy(i => i.ReceivedAt).Take(take).ToListAsync(ct);
}

public sealed class NotificationStore(EscrowDbContext db) : INotificationStore
{
    public void Add(Notification notification) => db.Notifications.Add(notification);
    public async Task<IReadOnlyList<Notification>> ListDueAsync(DateTimeOffset now, int take, CancellationToken ct) =>
        await db.Notifications.Where(n => n.Status == NotificationStatus.Pending && n.NextAttemptAt <= now).OrderBy(n => n.NextAttemptAt).Take(take).ToListAsync(ct);
    public async Task<IReadOnlyList<Notification>> ListForEscrowAsync(Guid escrowId, CancellationToken ct) =>
        await db.Notifications.AsNoTracking().Where(n => n.EscrowId == escrowId).OrderBy(n => n.CreatedAt).ToListAsync(ct);
}

public sealed class AuditLog(EscrowDbContext db) : IAuditLog
{
    public void Add(AuditEntry entry) => db.AuditLog.Add(entry);
    public async Task<IReadOnlyList<AuditEntry>> ListAsync(int take, CancellationToken ct) =>
        await db.AuditLog.AsNoTracking().OrderByDescending(a => a.CreatedAt).Take(take).ToListAsync(ct);
}

public sealed class ReconciliationStore(EscrowDbContext db) : IReconciliationStore
{
    public void Add(ReconciliationRun run) => db.ReconciliationRuns.Add(run);
    public void Add(ReconciliationItem item) => db.ReconciliationItems.Add(item);
    public async Task<IReadOnlyList<ReconciliationRun>> ListRunsAsync(int take, CancellationToken ct) =>
        await db.ReconciliationRuns.AsNoTracking().OrderByDescending(r => r.RanAt).Take(take).ToListAsync(ct);
    public async Task<IReadOnlyList<ReconciliationItem>> ListItemsAsync(Guid runId, CancellationToken ct) =>
        await db.ReconciliationItems.AsNoTracking().Where(i => i.RunId == runId).ToListAsync(ct);
}

public sealed class LedgerStore(EscrowDbContext db) : ILedgerStore
{
    public void Add(LedgerTransaction transaction) => db.LedgerTransactions.Add(transaction);

    public Task<long> GetBalanceAsync(string account, CancellationToken ct) => NetAsync(e => e.Account == account, ct);

    public async Task<LedgerSummary> GetSummaryAsync(string currency, CancellationToken ct) =>
        LedgerSummary.FromNets(
            currency,
            clearingNet: await NetAsync(e => e.Currency == currency && e.Account == Accounts.ProviderClearing, ct),
            escrowNet: await NetAsync(e => e.Currency == currency && e.Account.StartsWith("escrow:"), ct),
            sellerNet: await NetAsync(e => e.Currency == currency && e.Account.StartsWith("seller:"), ct),
            courierNet: await NetAsync(e => e.Currency == currency && e.Account.StartsWith("courier:"), ct),
            buyerNet: await NetAsync(e => e.Currency == currency && e.Account.StartsWith("buyer:"), ct),
            platformNet: await NetAsync(e => e.Currency == currency && e.Account == Accounts.PlatformRevenue, ct),
            feesNet: await NetAsync(e => e.Currency == currency && e.Account == Accounts.ProviderFees, ct));

    /// <summary>Credits minus debits over every entry matching the filter.</summary>
    private async Task<long> NetAsync(Expression<Func<LedgerEntry, bool>> filter, CancellationToken ct)
    {
        var entries = db.LedgerEntries.AsNoTracking().Where(filter);
        var credits = await entries.Where(e => e.Direction == Direction.Credit).SumAsync(e => (long?)e.Amount, ct) ?? 0;
        var debits = await entries.Where(e => e.Direction == Direction.Debit).SumAsync(e => (long?)e.Amount, ct) ?? 0;
        return credits - debits;
    }

    public async Task<IReadOnlyList<LedgerTransaction>> GetForEscrowAsync(Guid escrowId, CancellationToken ct) =>
        await db.LedgerTransactions.AsNoTracking().Include(t => t.Entries).Where(t => t.EscrowId == escrowId).OrderBy(t => t.CreatedAt).ToListAsync(ct);

    public async Task<IReadOnlyList<LedgerTransaction>> GetRecentAsync(int take, CancellationToken ct) =>
        await db.LedgerTransactions.AsNoTracking().Include(t => t.Entries).OrderByDescending(t => t.CreatedAt).Take(take).ToListAsync(ct);

    public async Task<IReadOnlyDictionary<Guid, long>> GetEscrowBalancesAsync(string currency, CancellationToken ct)
    {
        var rows = await db.LedgerEntries.AsNoTracking()
            .Where(e => e.Currency == currency && e.Account.StartsWith("escrow:"))
            .GroupBy(e => e.Account)
            .Select(g => new { Account = g.Key, Net = g.Sum(e => e.Direction == Direction.Credit ? e.Amount : -e.Amount) })
            .ToListAsync(ct);
        return rows.ToDictionary(r => Guid.Parse(r.Account["escrow:".Length..]), r => r.Net);
    }
}

public sealed class EfOutbox(EscrowDbContext db) : IOutbox
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public void Add(DomainEvent domainEvent) =>
        db.OutboxMessages.Add(new OutboxMessage
        {
            Id = domainEvent.EventId,
            Type = domainEvent.Type,
            Payload = JsonSerializer.Serialize(domainEvent, domainEvent.GetType(), Json),
            OccurredAt = domainEvent.OccurredAt
        });
}

public sealed class EfNumberSequence(EscrowDbContext db) : INumberSequence
{
    public async Task<long> NextAsync(CancellationToken ct) =>
        (await db.Database.SqlQuery<long>($"SELECT nextval('order_number_seq') AS \"Value\"").ToListAsync(ct)).Single();
}

/// <summary>
/// One SaveChanges = one database transaction: order state, order events, ledger rows, payouts, inbox marker,
/// notifications and outbox messages all commit together or not at all.
/// </summary>
public sealed class EfUnitOfWork(EscrowDbContext db) : IUnitOfWork
{
    public async Task CommitAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            throw new ConcurrencyConflictException("The order was modified by another request. Retry.", ex);
        }
        catch (DbUpdateException ex) when (
            ex.InnerException is PostgresException
            {
                SqlState: PostgresErrorCodes.UniqueViolation,
                ConstraintName: EscrowDbContext.LedgerIdempotencyIndex or EscrowDbContext.WebhookEventKeyIndex or EscrowDbContext.ClientEventIndex or EscrowDbContext.StaffUsernameIndex
            } pg)
        {
            throw new DuplicateOperationException($"Already recorded ({pg.ConstraintName}).", ex);
        }
    }
}
