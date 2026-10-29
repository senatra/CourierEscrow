using CourierEscrow.Application.Interfaces;
using CourierEscrow.Infrastructure.OneKhusa;

namespace CourierEscrow.Infrastructure.Payments;

public sealed class SwitchablePaymentProvider : IPaymentProvider, IPaymentProviderResolver
{
    private readonly MockPaymentProvider mock;
    private readonly OneKhusaProvider oneKhusa;
    private readonly OneKhusaOptions oneKhusaOptions;
    private IPaymentProvider active;

    public SwitchablePaymentProvider(
        MockPaymentProvider mock,
        OneKhusaProvider oneKhusa,
        OneKhusaOptions oneKhusaOptions,
        string initialProvider)
    {
        this.mock = mock;
        this.oneKhusa = oneKhusa;
        this.oneKhusaOptions = oneKhusaOptions;

        if (initialProvider.Equals("OneKhusa", StringComparison.OrdinalIgnoreCase))
        {
            oneKhusaOptions.Validate();
            active = oneKhusa;
        }
        else
        {
            active = mock;
        }
    }

    public IPaymentProvider Active => Volatile.Read(ref active);
    public MockPaymentProvider Mock => mock;
    public OneKhusaProvider OneKhusa => oneKhusa;
    public OneKhusaOptions OneKhusaOptions => oneKhusaOptions;
    public IReadOnlyList<string> MissingOneKhusaConfiguration => oneKhusaOptions.MissingConfiguration();
    public IPaymentProvider? GetProvider(string name) => string.Equals(name, mock.Name, StringComparison.OrdinalIgnoreCase) ? mock
        : string.Equals(name, oneKhusa.Name, StringComparison.OrdinalIgnoreCase) ? oneKhusa : null;

    public void Select(string name)
    {
        if (string.Equals(name, "Mock", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "Simulation", StringComparison.OrdinalIgnoreCase))
        {
            Volatile.Write(ref active, mock);
            return;
        }

        if (string.Equals(name, "OneKhusa", StringComparison.OrdinalIgnoreCase))
        {
            oneKhusaOptions.Validate();
            Volatile.Write(ref active, oneKhusa);
            return;
        }

        throw new ArgumentException("Choose Simulation or OneKhusa.", nameof(name));
    }

    public string Name => Active.Name;
    public bool SupportsPayoutInquiry => Active.SupportsPayoutInquiry;
    public string SignatureHeader => Active.SignatureHeader;
    public string EventHeader => Active.EventHeader;
    public Task<CollectionRequested> RequestCollectionAsync(CollectionRequest request, CancellationToken ct) => Active.RequestCollectionAsync(request, ct);
    public Task<CollectionStatusResult> GetCollectionStatusAsync(string reference, CancellationToken ct) => Active.GetCollectionStatusAsync(reference, ct);
    public Task<PayoutSubmission> SubmitPayoutAsync(PayoutInstruction instruction, CancellationToken ct) => Active.SubmitPayoutAsync(instruction, ct);
    public Task<PayoutStatusResult> GetPayoutStatusAsync(string reference, string? providerRef, DateOnly submittedOn, CancellationToken ct) => Active.GetPayoutStatusAsync(reference, providerRef, submittedOn, ct);
    public Task<IReadOnlyList<ProviderPayoutRecord>> ListPayoutsAsync(DateOnly day, CancellationToken ct) => Active.ListPayoutsAsync(day, ct);
    public Task<long?> GetBalanceAsync(string currency, CancellationToken ct) => Active.GetBalanceAsync(currency, ct);
    public Task<bool> VerifyWebhookAsync(byte[] rawBody, string? eventCode, string? signature, CancellationToken ct) => Active.VerifyWebhookAsync(rawBody, eventCode, signature, ct);
    public ProviderNotification? ParseWebhook(string? eventCode, string body) => Active.ParseWebhook(eventCode, body);
}
