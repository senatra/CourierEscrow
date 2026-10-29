namespace CourierEscrow.Web.Models;

public sealed record PaymentInfo(string Reference, string? AccountNumber, DateTimeOffset ExpiresAt, string State);
public sealed record PaymentProviderChoice(string Name, string Label, bool Available, List<string> MissingConfiguration, string? Environment, bool WebhooksReady = false, string? CurrencyCode = null, bool SandboxConnectorConfigured = false, bool PayoutConnectorsConfigured = false, string? WebhookCallbackUrl = null, List<string>? MissingWebhookSecrets = null);
public sealed record PaymentProviderSelection(string Selected, List<PaymentProviderChoice> Providers);
public sealed record DevSmsRow(DateTimeOffset At, string ToPhone, string Template, string Body);
