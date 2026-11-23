using VelocityGuard.Core.Payments;

namespace VelocityGuard.Tests.Payments;

public sealed class MoneyTests
{
    [Theory]
    [InlineData("EUR", "25.50", 2550)]
    [InlineData("EUR", "0.01", 1)]
    [InlineData("JPY", "1000", 1000)]
    [InlineData("BHD", "1.234", 1234)]
    [InlineData("GBP", "10", 1000)]
    public void Converts_major_units_to_exact_minor_units(string code, string amount, long expectedMinor)
    {
        var money = Money.FromMajor(decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture), Currency.Parse(code));

        Assert.Equal(expectedMinor, money.Minor);
        Assert.Equal(code, money.Currency.Code);
    }

    [Theory]
    [InlineData("EUR", "1.001", "at most 2 decimal places")]
    [InlineData("JPY", "10.5", "must be a whole number")]
    [InlineData("EUR", "0", "greater than zero")]
    [InlineData("EUR", "-5", "greater than zero")]
    [InlineData("EUR", "1000000000000", "maximum supported amount")]
    public void Rejects_amounts_the_currency_cannot_represent(string code, string amount, string expectedError)
    {
        var ok = Money.TryFromMajor(decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture), Currency.Parse(code), out _, out var error);

        Assert.False(ok);
        Assert.Contains(expectedError, error);
    }

    [Fact]
    public void Formats_with_invariant_grouping_and_the_currency_exponent()
    {
        Assert.Equal("1,250.00 EUR", Money.Format(125_000, Currency.Parse("EUR")));
        Assert.Equal("5,000 JPY", Money.Format(5_000, Currency.Parse("JPY")));
        Assert.Equal("1.500 BHD", Money.Format(1_500, Currency.Parse("BHD")));
    }

    [Fact]
    public void Currency_lookup_is_case_insensitive_and_canonicalises_the_code()
    {
        Assert.True(Currency.TryParse("eur", out var eur));
        Assert.Equal("EUR", eur.Code);
        Assert.False(Currency.TryParse("XXX", out _));
        Assert.False(Currency.TryParse("EURO", out _));
        Assert.False(Currency.TryParse(null, out _));
    }

    [Fact]
    public void Currency_ids_are_dense_and_unique()
    {
        var ids = Currency.Supported.Select(c => c.Id).Order().ToArray();

        Assert.Equal(Enumerable.Range(0, Currency.Count), ids);
    }
}
