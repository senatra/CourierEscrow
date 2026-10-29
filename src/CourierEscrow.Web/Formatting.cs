using System.Globalization;
using System.Text.RegularExpressions;

namespace CourierEscrow.Web;

public static partial class Fmt
{
    /// <summary>Minor units (tambala) to a display amount: 30800000 becomes "K308,000"; 150050 becomes "K1,500.50".</summary>
    public static string Money(long minor, string currency = "MWK")
    {
        var whole = minor % 100 == 0
            ? (minor / 100).ToString("N0", CultureInfo.InvariantCulture)
            : (minor / 100m).ToString("N2", CultureInfo.InvariantCulture);
        return (currency == "MWK" ? "K" : currency + " ") + whole;
    }

    /// <summary>Kwacha typed by a person ("308000" or "1,500.50") to tambala.</summary>
    public static bool TryParseKwacha(string? text, out long minor)
    {
        minor = 0;
        if (!decimal.TryParse(text?.Replace(",", "").Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var k) || k < 0) return false;
        minor = (long)Math.Round(k * 100m, MidpointRounding.AwayFromZero);
        return true;
    }

    public static string Short(Guid id) => id.ToString("N")[..8];

    [GeneratedRegex("[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")]
    private static partial Regex GuidPattern();

    /// <summary>"escrow:8f3a…-…" becomes "escrow:8f3a1c2d…" so ledger accounts stay readable.</summary>
    public static string Account(string account) => GuidPattern().Replace(account, m => m.Value[..8] + "…");

    public static string When(DateTimeOffset time) => time.UtcDateTime.ToString("dd MMM HH:mm:ss", CultureInfo.InvariantCulture);
    public static string When(DateTimeOffset? time) => time is null ? "—" : When(time.Value);
}
