using VelocityGuard.Core.Evaluation;
using VelocityGuard.Core.Payments;
using VelocityGuard.Core.Rules;
using static VelocityGuard.Tests.TestData;

namespace VelocityGuard.Tests.Rules;

public sealed class RuleExplainerTests
{
    [Theory]
    [InlineData(90, "90s")]
    [InlineData(600, "10m")]
    [InlineData(3600, "1h")]
    [InlineData(86_400, "24h")]
    [InlineData(129_600, "36h")]
    [InlineData(604_800, "7d")]
    public void Formats_windows_compactly(int seconds, string expected)
    {
        Assert.Equal(expected, RuleExplainer.FormatWindow(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void Explains_a_velocity_hit_with_counts_and_window()
    {
        var set = Compile(VelocityCount("device-burst", Dimension.Device, TimeSpan.FromMinutes(1), max: 3));
        var attempt = Card();
        var (_, results) = Evaluate(set, attempt, windows: [4]);

        var text = RuleExplainer.Explain(in set.Rules[0], results[0], attempt);

        Assert.Equal("4 attempts from this device in 1m (max 3)", text);
    }

    [Fact]
    public void Explains_money_in_the_attempt_currency()
    {
        var set = Compile(VelocitySum("player-24h", Dimension.Player, TimeSpan.FromDays(1), 2000m));
        var attempt = Card();
        var (_, results) = Evaluate(set, attempt, windows: [212_500]);

        var text = RuleExplainer.Explain(in set.Rules[0], results[0], attempt);

        Assert.Equal("2,125.00 EUR from this player in 24h (limit 2,000.00 EUR)", text);
    }

    [Fact]
    public void Explains_a_country_mismatch()
    {
        var set = Compile(new RuleDefinition { Id = "mismatch", Type = RuleType.CountryMismatch, Score = 25 });
        var attempt = Card(binCountry: "BR", ipCountry: "DE");
        var (_, results) = Evaluate(set, attempt);

        Assert.Equal("Card issued in BR, IP located in DE", RuleExplainer.Explain(in set.Rules[0], results[0], attempt));
    }
}
