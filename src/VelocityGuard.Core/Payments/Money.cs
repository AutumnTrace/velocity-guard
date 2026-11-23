using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace VelocityGuard.Core.Payments;

/// <summary>
/// An amount of money in integer minor units (cents, pence, yen) with an explicit currency.
/// Never a <see cref="double"/>: every comparison and sum in the engine is exact.
/// </summary>
public readonly record struct Money(long Minor, Currency Currency)
{
    /// <summary>Upper bound for a single amount: 10^13 minor units (100 billion EUR). Anything bigger is a bug.</summary>
    public const long MaxMinor = 10_000_000_000_000;

    /// <summary>
    /// Converts a decimal major-unit amount (<c>25.50</c>) to minor units, rejecting amounts that are
    /// not positive, have more decimals than the currency allows, or are absurdly large.
    /// </summary>
    public static bool TryFromMajor(decimal amount, Currency currency, out Money money, [NotNullWhen(false)] out string? error)
    {
        money = default;
        if (amount <= 0m)
        {
            error = "must be greater than zero";
            return false;
        }

        var scaled = amount * currency.MinorPerMajor;
        if (scaled != decimal.Truncate(scaled))
        {
            error = currency.MinorUnits == 0
                ? $"{currency.Code} has no minor unit, so the amount must be a whole number"
                : $"{currency.Code} allows at most {currency.MinorUnits} decimal places";
            return false;
        }

        if (scaled > MaxMinor)
        {
            error = "is larger than the maximum supported amount";
            return false;
        }

        money = new Money((long)scaled, currency);
        error = null;
        return true;
    }

    public static Money FromMajor(decimal amount, Currency currency) =>
        TryFromMajor(amount, currency, out var money, out var error)
            ? money
            : throw new ArgumentOutOfRangeException(nameof(amount), amount, $"Amount {error}.");

    public decimal ToMajor() => ToMajor(Minor, Currency);

    public static decimal ToMajor(long minor, Currency currency) => (decimal)minor / currency.MinorPerMajor;

    /// <summary>Formats minor units as <c>1,250.00 EUR</c> using the invariant culture.</summary>
    public static string Format(long minor, Currency currency) =>
        ToMajor(minor, currency).ToString("N" + currency.MinorUnits.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture)
        + " " + currency.Code;

    public override string ToString() => Format(Minor, Currency);
}
