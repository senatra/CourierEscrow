namespace CourierEscrow.Application.Helpers;

public sealed class EscrowOptions
{
    /// <summary>Platform fee in basis points of the ITEM amount (200 = 2%). Read once when an order is created and then stored on it.</summary>
    public int PlatformFeeBps { get; set; } = 200;

    /// <summary>How long a buyer has to pay before the order is cancelled.</summary>
    public int PaymentWindowMinutes { get; set; } = 15;

    public int OtpLifetimeHours { get; set; } = 24;

    /// <summary>Server secret mixed into every OTP hash. Must be set outside development.</summary>
    public string OtpSecret { get; set; } = "dev-only-otp-secret-change-me";

    public string TrackingBaseUrl { get; set; } = "https://nexa.mw/t/";
    public string DefaultCurrency { get; set; } = "MWK";
}
