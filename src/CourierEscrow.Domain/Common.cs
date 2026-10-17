using System.Security.Cryptography;
using System.Text;

namespace CourierEscrow.Domain;

public sealed record GeoPoint(double Latitude, double Longitude)
{
    public static GeoPoint? From(double? latitude, double? longitude) =>
        latitude is null || longitude is null ? null : new GeoPoint(latitude.Value, longitude.Value);

    public void Validate()
    {
        if (double.IsNaN(Latitude) || double.IsNaN(Longitude) || Latitude is < -90 or > 90 || Longitude is < -180 or > 180)
            throw new DomainValidationException("GPS position is out of range.");
    }
}

/// <summary>Malawian mobile numbers, stored as digits only in international form (265XXXXXXXXX).</summary>
public static class PhoneNumber
{
    public const string CountryCode = "265";

    public static string Normalize(string? input)
    {
        var digits = new string((input ?? "").Where(char.IsDigit).ToArray());
        if (digits.StartsWith("00")) digits = digits[2..];
        if (digits.Length == 10 && digits[0] == '0') digits = CountryCode + digits[1..];
        else if (digits.Length == 9) digits = CountryCode + digits;

        if (digits.Length != 12 || !digits.StartsWith(CountryCode))
            throw new DomainValidationException("Phone number must be a valid Malawian mobile number (e.g. +265 99 123 4567).");
        return digits;
    }

    /// <summary>"265991234567" becomes "****4567". Used for admin screens and logs.</summary>
    public static string Mask(string? msisdn) =>
        string.IsNullOrEmpty(msisdn) || msisdn.Length < 4 ? "****" : "****" + msisdn[^4..];
}

public static class References
{
    /// <summary>A fresh 16-character provider reference, e.g. ES3F9A0C12B7D44E.</summary>
    public static string New(string prefix) => prefix + Guid.NewGuid().ToString("N")[..14].ToUpperInvariant();

    /// <summary>A stable 16-character reference derived from a seed, so a retry produces the same value.</summary>
    public static string Derive(string prefix, string seed) =>
        prefix + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(seed)))[..14];

    /// <summary>Short, unguessable token used in the buyer's tracking link.</summary>
    public static string TrackingToken()
    {
        const string alphabet = "abcdefghijkmnopqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        return string.Create(8, 0, (span, _) => { for (var i = 0; i < span.Length; i++) span[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)]; });
    }
}
