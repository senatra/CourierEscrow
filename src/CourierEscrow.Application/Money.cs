using System.Globalization;

namespace CourierEscrow.Application;

public static class Money
{
    /// <summary>300000 tambala becomes "K3,000"; 150050 becomes "K1,500.50".</summary>
    public static string Format(long minor, string currency = "MWK")
    {
        var whole = minor % 100 == 0
            ? (minor / 100).ToString("N0", CultureInfo.InvariantCulture)
            : (minor / 100m).ToString("N2", CultureInfo.InvariantCulture);
        return (currency == "MWK" ? "K" : currency + " ") + whole;
    }

    public static decimal ToMajor(long minor) => minor / 100m;
    public static long ToMinor(decimal major) => (long)Math.Round(major * 100m, MidpointRounding.AwayFromZero);
}
