using CourierEscrow.Domain;
using CourierEscrow.Domain.Models;

namespace CourierEscrow.Application.Interfaces;

public interface IEscrowRepository
{
    Task<Escrow?> GetAsync(Guid id, CancellationToken ct);
    Task<Escrow?> GetByPaymentReferenceAsync(string reference, CancellationToken ct);
    Task<Escrow?> GetByTrackingTokenAsync(string token, CancellationToken ct);
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
    Task<bool> HasRunForDateAsync(DateOnly day, CancellationToken ct);
    Task<IReadOnlyList<ReconciliationItem>> ListItemsAsync(Guid runId, CancellationToken ct);
}
