using VelocityGuard.Core.Payments;

namespace VelocityGuard.Core.Rules;

/// <summary>
/// Per-currency limits in minor units, stored in an array indexed by <see cref="Currency.Id"/>
/// so a lookup on the hot path is a bounds check and a load.
/// </summary>
public sealed class CurrencyLimits
{
    public static readonly CurrencyLimits None = new(new Dictionary<Currency, long>());

    private readonly long[] _minorByCurrency;

    private CurrencyLimits(IReadOnlyDictionary<Currency, long> limits)
    {
        _minorByCurrency = new long[Currency.Count];
        Array.Fill(_minorByCurrency, -1);
        foreach (var (currency, minor) in limits)
        {
            _minorByCurrency[currency.Id] = minor;
        }

        IsEmpty = limits.Count == 0;
    }

    public bool IsEmpty { get; }

    public static CurrencyLimits FromMajor(IReadOnlyDictionary<string, decimal> limits)
    {
        if (limits.Count == 0)
        {
            return None;
        }

        var converted = new Dictionary<Currency, long>();
        foreach (var (code, amount) in limits)
        {
            var currency = Currency.Parse(code);
            converted[currency] = Money.FromMajor(amount, currency).Minor;
        }

        return new CurrencyLimits(converted);
    }

    public bool TryGet(Currency currency, out long minor)
    {
        minor = _minorByCurrency[currency.Id];
        return minor >= 0;
    }

    /// <summary>The limits back in major units, for display.</summary>
    public IReadOnlyDictionary<string, decimal> ToMajor()
    {
        var result = new SortedDictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var currency in Currency.Supported)
        {
            if (TryGet(currency, out var minor))
            {
                result[currency.Code] = Money.ToMajor(minor, currency);
            }
        }

        return result;
    }
}
