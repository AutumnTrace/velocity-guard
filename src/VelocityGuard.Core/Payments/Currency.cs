using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;

namespace VelocityGuard.Core.Payments;

/// <summary>
/// An ISO 4217 currency and its minor-unit exponent (EUR has 2 decimals, JPY none, BHD three).
/// </summary>
/// <remarks>
/// Each supported currency also gets a small dense <see cref="Id"/>, so per-currency limits
/// can be stored in plain arrays and looked up on the hot path without hashing a string.
/// </remarks>
public readonly record struct Currency
{
    private static readonly Currency[] All =
    [
        new("EUR", 2, 0), new("USD", 2, 1), new("GBP", 2, 2), new("CHF", 2, 3),
        new("SEK", 2, 4), new("NOK", 2, 5), new("DKK", 2, 6), new("PLN", 2, 7),
        new("CZK", 2, 8), new("HUF", 2, 9), new("RON", 2, 10), new("BGN", 2, 11),
        new("TRY", 2, 12), new("CAD", 2, 13), new("AUD", 2, 14), new("NZD", 2, 15),
        new("BRL", 2, 16), new("MXN", 2, 17), new("ZAR", 2, 18), new("INR", 2, 19),
        new("NGN", 2, 20), new("KES", 2, 21), new("JPY", 0, 22), new("KRW", 0, 23),
        new("CLP", 0, 24), new("ISK", 0, 25), new("BHD", 3, 26), new("KWD", 3, 27),
        new("TND", 3, 28), new("JOD", 3, 29),
    ];

    private static readonly FrozenDictionary<string, Currency> ByCode =
        All.ToFrozenDictionary(c => c.Code, StringComparer.OrdinalIgnoreCase);

    private Currency(string code, int minorUnits, int id)
    {
        Code = code;
        MinorUnits = minorUnits;
        Id = id;
    }

    /// <summary>Upper-case ISO 4217 alphabetic code, e.g. <c>EUR</c>.</summary>
    public string Code { get; }

    /// <summary>Number of decimal places in the minor unit (2 for EUR, 0 for JPY, 3 for BHD).</summary>
    public int MinorUnits { get; }

    /// <summary>Dense index in <c>[0, Count)</c>, stable for the lifetime of the process.</summary>
    public int Id { get; }

    /// <summary>How many currencies this build supports.</summary>
    public static int Count => All.Length;

    public static IReadOnlyList<Currency> Supported => All;

    public static bool TryParse([NotNullWhen(true)] string? code, out Currency currency)
    {
        if (code is { Length: 3 } && ByCode.TryGetValue(code, out currency))
        {
            return true;
        }

        currency = default;
        return false;
    }

    public static Currency Parse(string code) =>
        TryParse(code, out var currency)
            ? currency
            : throw new ArgumentException($"'{code}' is not a supported ISO 4217 currency.", nameof(code));

    /// <summary>10 to the power of <see cref="MinorUnits"/>: the number of minor units in one major unit.</summary>
    public long MinorPerMajor => MinorUnits switch
    {
        0 => 1,
        2 => 100,
        3 => 1000,
        _ => (long)Math.Pow(10, MinorUnits),
    };

    public override string ToString() => Code ?? "???";
}
