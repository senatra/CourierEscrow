namespace CourierEscrow.Application.Interfaces;

public sealed record CollectionRequest(string Reference, long Amount, string Currency, string BuyerPhone, string? BuyerNetwork, string Description);
public sealed record CollectionRequested(string? ProviderRef, string? AccountNumber, DateTimeOffset? ExpiresAt);

public enum RemoteStatus { Unknown, Pending, Succeeded, Failed, Reversed, RequiresReview }
public sealed record CollectionStatusResult(RemoteStatus Status, long? Amount, long Fee);
public sealed record PayoutStatusResult(RemoteStatus Status, long? Amount, long Fee, string? Reason);

public sealed record PayoutInstruction(string Reference, long Amount, string Currency, string BeneficiaryName, string Msisdn, string? Network, string Description);
public enum PayoutSubmissionOutcome { Accepted, Rejected, Transient }
public sealed record PayoutSubmission(PayoutSubmissionOutcome Outcome, string? ProviderRef, string? Message);
public sealed record ProviderPayoutRecord(string TransactionRef, string? SourceReference, long Amount, RemoteStatus Status, long Fee = 0, string? Currency = null, string? StatusCode = null);

public enum CollectionEventKind { Succeeded, Failed, Reversed }
public enum PayoutEventKind { Succeeded, Failed, Reversed }

public abstract record ProviderNotification(string EventKey);
public sealed record CollectionNotification(string EventKey, string Reference, CollectionEventKind Kind, long Amount, long Fee, string Currency, string? ProviderRef)
    : ProviderNotification(EventKey);
public sealed record PayoutNotification(string EventKey, string SourceReference, PayoutEventKind Kind, long Amount, long Fee, string? Reason, string? ProviderRef)
    : ProviderNotification(EventKey);
