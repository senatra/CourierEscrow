using CourierEscrow.Domain;
using CourierEscrow.Domain.Ledger;

namespace CourierEscrow.Application;

public interface IEscrowRepository
{
    Task<Escrow?> GetAsync(Guid id, CancellationToken ct);
    Task<Escrow?> GetByPaymentReferenceAsync(string reference, CancellationToken ct);
    Task<Escrow?> GetByTrackingTokenAsync(string token, CancellationToken ct);

    /// <summary>Most recently created first.</summary>
    Task<IReadOnlyList<Escrow>> ListAsync(int take, EscrowState? state, CancellationToken ct);
    Task<IReadOnlyList<Escrow>> ListByStatesAsync(IReadOnlyCollection<EscrowState> states, int take, CancellationToken ct);
    Task<IReadOnlyList<Escrow>> ListForCourierAsync(Guid courierId, CancellationToken ct);
    Task<IReadOnlyList<Escrow>> ListFlaggedAsync(int take, CancellationToken ct);
    void Add(Escrow escrow);
}

public interface IPartyRepository
{
    Task<Party?> FindAsync(PartyRole role, string phone, CancellationToken ct);
    Task<Party?> GetAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<Party>> GetManyAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct);
    void Add(Party party);
}

public interface ICourierRepository
{
    Task<CourierAccount?> GetAsync(Guid id, CancellationToken ct);
    Task<CourierAccount?> FindByPhoneAsync(string phone, CancellationToken ct);
    Task<IReadOnlyList<CourierAccount>> ListAsync(CancellationToken ct);
    void Add(CourierAccount courier);
}

public interface IStaffUserRepository
{
    Task<StaffUser?> GetAsync(Guid id, CancellationToken ct);
    Task<StaffUser?> FindByUsernameAsync(string username, CancellationToken ct);
    Task<IReadOnlyList<StaffUser>> ListAsync(CancellationToken ct);
    Task<bool> AnyAsync(CancellationToken ct);
    Task<int> CountActiveAdminsAsync(CancellationToken ct);
    void Add(StaffUser user);
}

public interface IPaymentIntentRepository
{
    void Add(PaymentIntent intent);
    Task<PaymentIntent?> GetByReferenceAsync(string reference, CancellationToken ct);
    Task<IReadOnlyList<PaymentIntent>> ListForEscrowAsync(Guid escrowId, CancellationToken ct);

    /// <summary>Intents still waiting (Initiated or Pending), oldest first.</summary>
    Task<IReadOnlyList<PaymentIntent>> ListOpenAsync(int take, CancellationToken ct);
}

public interface IPayoutRepository
{
    void Add(Payout payout);
    Task<Payout?> GetAsync(Guid id, CancellationToken ct);
    Task<Payout?> GetByReferenceAsync(string reference, CancellationToken ct);
    Task<IReadOnlyList<Payout>> ListForEscrowAsync(Guid escrowId, CancellationToken ct);
    Task<IReadOnlyList<Payout>> ListDueAsync(DateTimeOffset now, int take, CancellationToken ct);
    Task<IReadOnlyList<Payout>> ListSubmittedBeforeAsync(DateTimeOffset cutoff, int take, CancellationToken ct);
    Task<IReadOnlyList<Payout>> ListAsync(PayoutStatus? status, int take, CancellationToken ct);

    /// <summary>Payouts submitted on a UTC day, for reconciliation.</summary>
    Task<IReadOnlyList<Payout>> ListSubmittedOnAsync(DateOnly day, CancellationToken ct);
}

public interface IOtpRepository
{
    Task<Otp?> GetAsync(Guid escrowId, CancellationToken ct);
    void Add(Otp otp);
}

public interface IProofRepository
{
    void Add(Proof proof);
    Task<IReadOnlyList<Proof>> ListForEscrowAsync(Guid escrowId, CancellationToken ct);
}

public interface IUploadRepository
{
    void Add(Upload upload);
    Task<Upload?> GetAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<Upload>> GetManyAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct);
}

public interface IDisputeRepository
{
    void Add(Dispute dispute);
    Task<Dispute?> GetAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<Dispute>> ListAsync(DisputeStatus? status, int take, CancellationToken ct);
    Task<IReadOnlyList<Dispute>> ListForEscrowAsync(Guid escrowId, CancellationToken ct);
}

public interface IOrderEventStore
{
    void Add(OrderEvent orderEvent);
    Task<IReadOnlyList<OrderEvent>> ListForEscrowAsync(Guid escrowId, CancellationToken ct);
    Task<bool> ExistsAsync(Guid escrowId, string clientEventId, CancellationToken ct);
}

public interface IWebhookInbox
{
    void Add(WebhookInboxItem item);
    Task<WebhookInboxItem?> GetAsync(Guid id, CancellationToken ct);
    Task<bool> ExistsAsync(string eventKey, CancellationToken ct);
    Task<IReadOnlyList<WebhookInboxItem>> ListPendingAsync(int take, CancellationToken ct);
}

public interface INotificationStore
{
    void Add(Notification notification);
    Task<IReadOnlyList<Notification>> ListDueAsync(DateTimeOffset now, int take, CancellationToken ct);
    Task<IReadOnlyList<Notification>> ListForEscrowAsync(Guid escrowId, CancellationToken ct);
}

/// <summary>In-memory copy of SMS that were actually sent. Development uses this so ops can read the buyer code without opening logs.</summary>
public sealed record CapturedSms(DateTimeOffset At, Guid? EscrowId, string ToPhone, string Template, string Body);

public interface ISmsCapture
{
    void Record(Guid? escrowId, string toPhone, string template, string body, DateTimeOffset at);
    IReadOnlyList<CapturedSms> Recent(int take = 100);
}

public sealed class NullSmsCapture : ISmsCapture
{
    public void Record(Guid? escrowId, string toPhone, string template, string body, DateTimeOffset at) { }
    public IReadOnlyList<CapturedSms> Recent(int take = 100) => [];
}

public interface IAuditLog
{
    void Add(AuditEntry entry);
    Task<IReadOnlyList<AuditEntry>> ListAsync(int take, CancellationToken ct);
}

public interface IReconciliationStore
{
    void Add(ReconciliationRun run);
    void Add(ReconciliationItem item);
    Task<IReadOnlyList<ReconciliationRun>> ListRunsAsync(int take, CancellationToken ct);
    Task<IReadOnlyList<ReconciliationItem>> ListItemsAsync(Guid runId, CancellationToken ct);
}

public interface ILedgerStore
{
    void Add(LedgerTransaction transaction);

    /// <summary>Net balance as credits minus debits (so asset accounts such as provider clearing read negative).</summary>
    Task<long> GetBalanceAsync(string account, CancellationToken ct);
    Task<IReadOnlyList<LedgerTransaction>> GetForEscrowAsync(Guid escrowId, CancellationToken ct);
    Task<IReadOnlyList<LedgerTransaction>> GetRecentAsync(int take, CancellationToken ct);
    Task<LedgerSummary> GetSummaryAsync(string currency, CancellationToken ct);

    /// <summary>Balance of every escrow account (credits minus debits), keyed by order id.</summary>
    Task<IReadOnlyDictionary<Guid, long>> GetEscrowBalancesAsync(string currency, CancellationToken ct);
}

/// <summary>
/// Where all the money in the system sits. Every figure is a positive amount in minor units.
/// The books balance when <c>ProviderBalance = Escrow + payables + PlatformRevenue - ProviderFees</c>.
/// </summary>
public sealed record LedgerSummary(
    string Currency, long ProviderBalance, long HeldInEscrow, long OwedToSellers, long OwedToCouriers,
    long OwedToBuyers, long PlatformRevenue, long ProviderFees)
{
    public bool IsBalanced =>
        ProviderBalance == HeldInEscrow + OwedToSellers + OwedToCouriers + OwedToBuyers + PlatformRevenue - ProviderFees;

    /// <summary>Builds a summary from net balances (credits minus debits), flipping the sign of the two debit-normal accounts.</summary>
    public static LedgerSummary FromNets(string currency, long clearingNet, long escrowNet, long sellerNet,
        long courierNet, long buyerNet, long platformNet, long feesNet) =>
        new(currency, -clearingNet, escrowNet, sellerNet, courierNet, buyerNet, platformNet, -feesNet);
}

/// <summary>Stages domain events to be published later, atomically with the state change (transactional outbox).</summary>
public interface IOutbox
{
    void Add(DomainEvent domainEvent);
}

/// <summary>Everything staged through the repositories commits in ONE transaction.</summary>
public interface IUnitOfWork
{
    /// <exception cref="ConcurrencyConflictException">Someone else changed the same row first.</exception>
    /// <exception cref="DuplicateOperationException">A webhook event, client event or ledger idempotency key was already recorded.</exception>
    Task CommitAsync(CancellationToken ct);
}

public interface INumberSequence
{
    Task<long> NextAsync(CancellationToken ct);
}

/// <summary>Encrypts small secrets (the OTP in a queued SMS) at rest.</summary>
public interface IProtector
{
    string Protect(string plain);
    string Unprotect(string protectedValue);
}

public interface ISmsSender
{
    /// <summary>Returns the aggregator's message id. Throws when the message could not be handed over.</summary>
    Task<string?> SendAsync(string toPhone, string body, CancellationToken ct);
}

public sealed record UploadTarget(string Url, DateTimeOffset ExpiresAt);

public interface IObjectStorage
{
    /// <summary>A short-lived URL the courier's phone can PUT a photo to.</summary>
    UploadTarget CreateUploadTarget(Guid uploadId, DateTimeOffset now);
    bool ValidateUploadTarget(Guid uploadId, long expiresUnix, string? signature, DateTimeOffset now);
    Task SaveAsync(string objectKey, Stream content, CancellationToken ct);
    Task<Stream?> OpenAsync(string objectKey, CancellationToken ct);
}
