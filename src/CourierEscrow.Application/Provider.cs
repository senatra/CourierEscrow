using CourierEscrow.Domain;

namespace CourierEscrow.Application;

public sealed record CollectionRequest(string Reference, long Amount, string Currency, string BuyerPhone, string? BuyerNetwork, string Description);

/// <summary>What the provider gave back for a collection. <see cref="AccountNumber"/> is where the buyer pays (OneKhusa's timed account number).</summary>
public sealed record CollectionRequested(string? ProviderRef, string? AccountNumber, DateTimeOffset? ExpiresAt);

public enum RemoteStatus { Unknown, Pending, Succeeded, Failed }
public sealed record CollectionStatusResult(RemoteStatus Status, long? Amount, long Fee);
public sealed record PayoutStatusResult(RemoteStatus Status, long? Amount, long Fee, string? Reason);

public sealed record PayoutInstruction(string Reference, long Amount, string Currency, string BeneficiaryName, string Msisdn, string? Network, string Description);
public enum PayoutSubmissionOutcome { Accepted, Rejected, Transient }
public sealed record PayoutSubmission(PayoutSubmissionOutcome Outcome, string? ProviderRef, string? Message);

public sealed record ProviderPayoutRecord(string TransactionRef, string? SourceReference, long Amount, RemoteStatus Status);

public enum CollectionEventKind { Succeeded, Failed, Reversed }
public enum PayoutEventKind { Succeeded, Failed, Reversed }

public abstract record ProviderNotification(string EventKey);
public sealed record CollectionNotification(string EventKey, string Reference, CollectionEventKind Kind, long Amount, long Fee, string Currency, string? ProviderRef)
    : ProviderNotification(EventKey);
public sealed record PayoutNotification(string EventKey, string SourceReference, PayoutEventKind Kind, long Amount, long Fee, string? Reason, string? ProviderRef)
    : ProviderNotification(EventKey);

/// <summary>
/// The only seam to a payment provider. Every mutating call carries a stable reference that the provider uses as
/// an idempotency key, so a retry after a timeout can never charge or pay twice.
/// </summary>
public interface IPaymentProvider
{
    string Name { get; }
    string SignatureHeader { get; }
    string EventHeader { get; }

    Task<CollectionRequested> RequestCollectionAsync(CollectionRequest request, CancellationToken ct);
    Task<CollectionStatusResult> GetCollectionStatusAsync(string reference, CancellationToken ct);

    Task<PayoutSubmission> SubmitPayoutAsync(PayoutInstruction instruction, CancellationToken ct);
    Task<PayoutStatusResult> GetPayoutStatusAsync(string reference, string? providerRef, DateOnly submittedOn, CancellationToken ct);

    /// <summary>The provider's own record of payouts for a day, for reconciliation.</summary>
    Task<IReadOnlyList<ProviderPayoutRecord>> ListPayoutsAsync(DateOnly day, CancellationToken ct);

    /// <summary>The provider's reported balance in minor units, or null when it cannot say.</summary>
    Task<long?> GetBalanceAsync(string currency, CancellationToken ct);

    bool VerifyWebhook(byte[] rawBody, string? signature);

    /// <summary>Null when the event is not one we act on.</summary>
    ProviderNotification? ParseWebhook(string? eventCode, string body);
}
