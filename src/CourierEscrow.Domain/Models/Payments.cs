namespace CourierEscrow.Domain.Models;

public enum PaymentIntentStatus { Initiated = 0, Pending = 1, Succeeded = 2, Failed = 3, Expired = 4, Mismatch = 5 }

/// <summary>One attempt to collect the buyer's money. The reference is our idempotency key with the provider.</summary>
public sealed class PaymentIntent
{
    private PaymentIntent() { Reference = ""; Currency = ""; }

    public Guid Id { get; private set; }
    public Guid EscrowId { get; private set; }
    public string Reference { get; private set; }
    public string? ProviderRef { get; private set; }
    public string? CollectionProviderRef { get; private set; }
    public long Amount { get; private set; }
    public string Currency { get; private set; }
    public PaymentIntentStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public DateTimeOffset? LastPolledAt { get; private set; }

    public static PaymentIntent Create(Escrow e, string reference, DateTimeOffset expiresAt, DateTimeOffset now) =>
        new() { Id = Guid.NewGuid(), EscrowId = e.Id, Reference = reference, Amount = e.Total, Currency = e.Currency,
                Status = PaymentIntentStatus.Initiated, CreatedAt = now, ExpiresAt = expiresAt };

    public bool IsOpen => Status is PaymentIntentStatus.Initiated or PaymentIntentStatus.Pending;

    public void MarkPending(string? providerRef, DateTimeOffset? expiresAt)
    {
        if (Status != PaymentIntentStatus.Initiated && Status != PaymentIntentStatus.Pending) return;
        ProviderRef = providerRef ?? ProviderRef;
        if (expiresAt is not null) ExpiresAt = expiresAt.Value;
        Status = PaymentIntentStatus.Pending;
    }

    public void Succeed(string? collectionProviderRef = null)
    {
        CollectionProviderRef = collectionProviderRef ?? CollectionProviderRef;
        Status = PaymentIntentStatus.Succeeded;
    }
    public void Fail() => Status = PaymentIntentStatus.Failed;
    public void Expire() => Status = PaymentIntentStatus.Expired;
    public void FlagMismatch() => Status = PaymentIntentStatus.Mismatch;
    public void Polled(DateTimeOffset now) => LastPolledAt = now;
}

public enum PaymentAttemptOutcome { Started = 0, Succeeded = 1, Failed = 2 }

public sealed class PaymentAttempt
{
    private PaymentAttempt() { Provider = ""; IdempotencyKey = ""; }

    public Guid Id { get; private set; }
    public Guid PaymentIntentId { get; private set; }
    public string Provider { get; private set; }
    public string IdempotencyKey { get; private set; }
    public string? RequestFingerprint { get; private set; }
    public DateTimeOffset StartedAt { get; private set; }
    public DateTimeOffset? FinishedAt { get; private set; }
    public PaymentAttemptOutcome Outcome { get; private set; }
    public int? ProviderStatusCode { get; private set; }
    public string? FailureMessage { get; private set; }
    public bool? TransientFailure { get; private set; }

    public static PaymentAttempt Start(Guid paymentIntentId, string provider, string idempotencyKey, DateTimeOffset now) =>
        new()
        {
            Id = Guid.NewGuid(), PaymentIntentId = paymentIntentId, Provider = provider,
            IdempotencyKey = idempotencyKey, StartedAt = now, Outcome = PaymentAttemptOutcome.Started
        };

    public void Succeed(string? requestFingerprint, int? providerStatusCode, DateTimeOffset now)
    {
        if (Outcome != PaymentAttemptOutcome.Started) return;
        RequestFingerprint = requestFingerprint;
        ProviderStatusCode = providerStatusCode;
        FinishedAt = now;
        Outcome = PaymentAttemptOutcome.Succeeded;
    }

    public void Fail(string? requestFingerprint, int? providerStatusCode, string message, bool transient, DateTimeOffset now)
    {
        if (Outcome != PaymentAttemptOutcome.Started) return;
        RequestFingerprint = requestFingerprint;
        ProviderStatusCode = providerStatusCode;
        FailureMessage = message.Length <= 1000 ? message : message[..1000];
        TransientFailure = transient;
        FinishedAt = now;
        Outcome = PaymentAttemptOutcome.Failed;
    }
}

public enum PayoutKind { Seller = 0, Courier = 1, BuyerRefund = 2 }
public enum PayoutStatus { Queued = 0, Submitted = 1, Confirmed = 2, NeedsReview = 3 }

/// <summary>
/// Money leaving the platform to one wallet. Created in the same transaction as the settlement posting.
/// Only a confirmed payout clears the matching payable in the ledger.
/// </summary>
public sealed class Payout
{
    public const int MaxAttempts = 5;
    private static readonly TimeSpan[] Backoff = [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), TimeSpan.FromHours(1)];

    private Payout() { Currency = ""; Msisdn = ""; BeneficiaryName = ""; Reference = ""; }

    public Guid Id { get; private set; }
    public Guid EscrowId { get; private set; }
    public PayoutKind Kind { get; private set; }

    /// <summary>Seller or buyer party id, or the courier account id. Names the payable account this payout clears.</summary>
    public Guid PayeeId { get; private set; }
    public string BeneficiaryName { get; private set; }
    public string Msisdn { get; private set; }
    public string? Network { get; private set; }
    public long Amount { get; private set; }
    public string Currency { get; private set; }

    /// <summary>Our reference and the provider idempotency key. Same value on retries; a new generation gets a new one.</summary>
    public string Reference { get; private set; }
    public int KeyGeneration { get; private set; }
    public string? ProviderRef { get; private set; }
    public PayoutStatus Status { get; private set; }
    public int Attempts { get; private set; }
    public DateTimeOffset NextAttemptAt { get; private set; }
    public string? LastError { get; private set; }
    public long Fee { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? SubmittedAt { get; private set; }
    public DateTimeOffset? ConfirmedAt { get; private set; }

    public static Payout Create(Escrow e, PayoutKind kind, Guid payeeId, string name, string msisdn, string? network, long amount, DateTimeOffset now)
    {
        if (amount <= 0) throw new DomainValidationException("Payout amount must be positive.");
        var p = new Payout
        {
            Id = Guid.NewGuid(), EscrowId = e.Id, Kind = kind, PayeeId = payeeId, BeneficiaryName = name, Msisdn = msisdn,
            Network = network, Amount = amount, Currency = e.Currency, CreatedAt = now, NextAttemptAt = now
        };
        p.Reference = p.KeyFor(0);
        if (network is null) p.NeedsAttention("No wallet network on file for this payee.");
        return p;
    }

    private string KeyFor(int generation) => References.Derive("PO", $"{EscrowId}:{Kind}:{generation}");

    public string PayableAccountKey => Kind switch { PayoutKind.Seller => "seller", PayoutKind.Courier => "courier", _ => "buyer" };

    /// <summary>The provider took the request. Now we wait for the webhook (or a poll) to confirm.</summary>
    public void MarkSubmitted(string? providerRef, DateTimeOffset now)
    {
        Status = PayoutStatus.Submitted; ProviderRef = providerRef; SubmittedAt = now; Attempts++; LastError = null;
    }

    /// <summary>Timeout, 5xx or rate limit. Retry with the same key so the provider cannot pay twice.</summary>
    public void RecordTransientFailure(string error, DateTimeOffset now)
    {
        Attempts++; LastError = error;
        if (Attempts >= MaxAttempts) { Status = PayoutStatus.NeedsReview; return; }
        Status = PayoutStatus.Queued;
        NextAttemptAt = now + Backoff[Math.Min(Attempts - 1, Backoff.Length - 1)];
    }

    /// <summary>The provider refused outright (bad account, validation). Retrying the same request will not help.</summary>
    public void NeedsAttention(string reason) { Status = PayoutStatus.NeedsReview; LastError = reason; }

    /// <summary>The provider accepted, then reported failure. Try again under a fresh key, up to the limit.</summary>
    public void FailedAtProvider(string reason, DateTimeOffset now)
    {
        Attempts++; LastError = reason;
        if (Attempts >= MaxAttempts) { Status = PayoutStatus.NeedsReview; return; }
        KeyGeneration++;
        Reference = KeyFor(KeyGeneration);
        ProviderRef = null;
        Status = PayoutStatus.Queued;
        NextAttemptAt = now + Backoff[Math.Min(Attempts - 1, Backoff.Length - 1)];
    }

    public void Confirm(long fee, string? providerRef, DateTimeOffset now)
    {
        Status = PayoutStatus.Confirmed; Fee = fee; ProviderRef = providerRef ?? ProviderRef; ConfirmedAt = now; LastError = null;
    }

    /// <summary>The provider reversed a payout we thought was done. Needs a human.</summary>
    public void Reversed(string reason) { Status = PayoutStatus.NeedsReview; LastError = reason; }

    /// <summary>Ops pressed retry. A new key is used when the earlier request was refused or reversed.</summary>
    public void Retry(bool newKey, DateTimeOffset now)
    {
        if (Status != PayoutStatus.NeedsReview) throw new DomainValidationException("Only payouts that need review can be retried.");
        if (newKey) { KeyGeneration++; Reference = KeyFor(KeyGeneration); ProviderRef = null; }
        Attempts = 0; LastError = null; Status = PayoutStatus.Queued; NextAttemptAt = now;
    }

    public void CorrectWallet(string msisdn, string network)
    {
        Msisdn = PhoneNumber.Normalize(msisdn); Network = network;
    }
}

public enum WebhookStatus { Pending = 0, Processed = 1, Failed = 2 }

/// <summary>A verified provider callback, stored raw before anything acts on it so nothing is lost and replays are recognised.</summary>
public sealed class WebhookInboxItem
{
    private WebhookInboxItem() { Provider = ""; EventKey = ""; EventCode = ""; RawPayload = ""; }

    public Guid Id { get; private set; }
    public string Provider { get; private set; }

    /// <summary>Unique. Built from the event code and the provider's transaction id.</summary>
    public string EventKey { get; private set; }
    public string EventCode { get; private set; }
    public string RawPayload { get; private set; }
    public WebhookStatus Status { get; private set; }
    public string? Outcome { get; private set; }
    public int Attempts { get; private set; }
    public string? LastError { get; private set; }
    public DateTimeOffset ReceivedAt { get; private set; }
    public DateTimeOffset? ProcessedAt { get; private set; }

    public static WebhookInboxItem Create(string provider, string eventKey, string eventCode, string raw, DateTimeOffset now) =>
        new() { Id = Guid.NewGuid(), Provider = provider, EventKey = eventKey, EventCode = eventCode, RawPayload = raw, ReceivedAt = now };

    public void MarkProcessed(string outcome, DateTimeOffset now) { Status = WebhookStatus.Processed; Outcome = outcome; ProcessedAt = now; LastError = null; }
    public void MarkFailed(string error) { Attempts++; LastError = error; Status = Attempts >= 10 ? WebhookStatus.Failed : WebhookStatus.Pending; }
}
