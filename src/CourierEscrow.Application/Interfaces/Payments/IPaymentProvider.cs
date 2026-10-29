namespace CourierEscrow.Application.Interfaces;

public interface IPaymentProvider
{
    string Name { get; }
    bool SupportsPayoutInquiry => false;
    string SignatureHeader { get; }
    string EventHeader { get; }
    Task<CollectionRequested> RequestCollectionAsync(CollectionRequest request, CancellationToken ct);
    Task<CollectionStatusResult> GetCollectionStatusAsync(string reference, CancellationToken ct);
    Task<PayoutSubmission> SubmitPayoutAsync(PayoutInstruction instruction, CancellationToken ct);
    Task<PayoutStatusResult> GetPayoutStatusAsync(string reference, string? providerRef, DateOnly submittedOn, CancellationToken ct);
    Task<IReadOnlyList<ProviderPayoutRecord>> ListPayoutsAsync(DateOnly day, CancellationToken ct);
    Task<long?> GetBalanceAsync(string currency, CancellationToken ct);
    Task<bool> VerifyWebhookAsync(byte[] rawBody, string? eventCode, string? signature, CancellationToken ct);
    ProviderNotification? ParseWebhook(string? eventCode, string body);
}

public interface IPaymentProviderResolver
{
    IPaymentProvider? GetProvider(string name);
}
