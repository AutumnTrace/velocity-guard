using VelocityGuard.Core.Evaluation;
using VelocityGuard.Core.Payments;
using VelocityGuard.Core.Rules;
using VelocityGuard.Core.Signals;
using static VelocityGuard.Tests.TestData;

namespace VelocityGuard.Tests.Rules;

/// <summary>One section per rule type: when it hits, when it passes, when it does not apply, when it is skipped.</summary>
public sealed class RuleEvaluationTests
{
    [Theory]
    [InlineData(3, RuleStatus.Passed)]
    [InlineData(4, RuleStatus.Hit)]
    public void Velocity_count_hits_above_the_max(long count, RuleStatus expected)
    {
        var set = Compile(VelocityCount("device-burst", Dimension.Device, TimeSpan.FromMinutes(1), max: 3));

        var (_, results) = Evaluate(set, Card(), windows: [count]);

        Assert.Equal(expected, results[0].Status);
        Assert.Equal(count, results[0].Observed);
        Assert.Equal(3, results[0].Limit);
    }

    [Fact]
    public void Velocity_count_does_not_apply_without_the_subject()
    {
        var set = Compile(VelocityCount("device-burst", Dimension.Device, TimeSpan.FromMinutes(1), max: 3));

        var (_, results) = Evaluate(set, Card(device: null), windows: [10]);

        Assert.Equal(RuleStatus.NotApplicable, results[0].Status);
    }

    [Fact]
    public void Velocity_rules_are_skipped_when_the_store_is_unavailable()
    {
        var set = Compile(
            VelocityCount("device-burst", Dimension.Device, TimeSpan.FromMinutes(1), max: 3),
            Amount("large", 100m));

        var (summary, results) = Evaluate(set, Card(amount: 150m), windows: [99], available: SignalAvailability.Lists | SignalAvailability.Profile);

        Assert.Equal(RuleStatus.Skipped, results[0].Status);
        Assert.Equal(RuleStatus.Hit, results[1].Status);
        Assert.Equal(1, summary.Skipped);
    }

    [Theory]
    [InlineData(200_000, RuleStatus.Passed)]
    [InlineData(200_001, RuleStatus.Hit)]
    public void Velocity_sum_hits_above_the_limit_in_minor_units(long sumMinor, RuleStatus expected)
    {
        var set = Compile(VelocitySum("player-24h", Dimension.Player, TimeSpan.FromDays(1), eurLimit: 2000m));

        var (_, results) = Evaluate(set, Card(), windows: [sumMinor]);

        Assert.Equal(expected, results[0].Status);
    }

    [Fact]
    public void Velocity_sum_does_not_apply_to_a_currency_without_a_limit()
    {
        var set = Compile(VelocitySum("player-24h", Dimension.Player, TimeSpan.FromDays(1), eurLimit: 2000m));

        var (_, results) = Evaluate(set, Card(currency: "GBP"), windows: [long.MaxValue]);

        Assert.Equal(RuleStatus.NotApplicable, results[0].Status);
    }

    [Theory]
    [InlineData(4999.99, RuleStatus.Passed)]
    [InlineData(5000, RuleStatus.Hit)]
    public void Amount_threshold_hits_at_or_above_the_limit(double amount, RuleStatus expected)
    {
        var set = Compile(Amount("large", 5000m));

        var (_, results) = Evaluate(set, Card(amount: (decimal)amount));

        Assert.Equal(expected, results[0].Status);
    }

    [Theory]
    [InlineData("DE", "DE", RuleStatus.Passed)]
    [InlineData("DE", "GB", RuleStatus.Hit)]
    public void Country_mismatch_compares_bin_and_ip_country(string bin, string ipCountry, RuleStatus expected)
    {
        var set = Compile(new RuleDefinition { Id = "mismatch", Type = RuleType.CountryMismatch, Score = 25 });

        var (_, results) = Evaluate(set, Card(binCountry: bin, ipCountry: ipCountry));

        Assert.Equal(expected, results[0].Status);
    }

    [Fact]
    public void Country_mismatch_only_applies_to_cards()
    {
        var set = Compile(new RuleDefinition { Id = "mismatch", Type = RuleType.CountryMismatch, Score = 25 });

        var (_, results) = Evaluate(set, Wallet());

        Assert.Equal(RuleStatus.NotApplicable, results[0].Status);
    }

    [Theory]
    [InlineData(2, 500, RuleStatus.Hit)]
    [InlineData(2, 499.99, RuleStatus.Passed)]
    [InlineData(7, 900, RuleStatus.Passed)]
    public void New_account_amount_needs_a_young_account_and_a_large_amount(int ageDays, double amount, RuleStatus expected)
    {
        var set = Compile(new RuleDefinition
        {
            Id = "new-account",
            Type = RuleType.NewAccountAmount,
            MaxAccountAgeDays = 7,
            Limits = new() { ["EUR"] = 500m },
            Score = 30,
        });

        var (_, results) = Evaluate(set, Card(amount: (decimal)amount, accountAgeDays: ageDays));

        Assert.Equal(expected, results[0].Status);
    }

    [Fact]
    public void First_deposit_uses_the_profile_and_is_skipped_without_it()
    {
        var set = Compile(new RuleDefinition { Id = "first", Type = RuleType.FirstDeposit, Limits = new() { ["EUR"] = 1000m }, Score = 20 });
        var attempt = Card(amount: 1000m);

        Assert.Equal(RuleStatus.Hit, Evaluate(set, attempt, profile: PlayerProfile.New).Results[0].Status);
        Assert.Equal(RuleStatus.Passed, Evaluate(set, attempt, profile: new PlayerProfile(3, 0, null)).Results[0].Status);
        Assert.Equal(RuleStatus.Passed, Evaluate(set, Card(amount: 999m), profile: PlayerProfile.New).Results[0].Status);
        Assert.Equal(RuleStatus.Skipped, Evaluate(set, attempt, available: SignalAvailability.Velocity | SignalAvailability.Lists).Results[0].Status);
    }

    [Fact]
    public void First_deposit_without_limits_applies_to_any_amount_and_currency()
    {
        var set = Compile(new RuleDefinition { Id = "first", Type = RuleType.FirstDeposit, Score = 10 });

        var (_, results) = Evaluate(set, Card(amount: 1m, currency: "JPY"), profile: PlayerProfile.New);

        Assert.Equal(RuleStatus.Hit, results[0].Status);
    }

    [Theory]
    [InlineData(0, RuleStatus.Passed)]
    [InlineData(1, RuleStatus.Hit)]
    [InlineData(4, RuleStatus.Hit)]
    public void Chargeback_history_hits_at_the_minimum(int chargebacks, RuleStatus expected)
    {
        var set = Compile(new RuleDefinition { Id = "cb", Type = RuleType.ChargebackHistory, MinChargebacks = 1, Score = 60 });

        var (_, results) = Evaluate(set, Card(), profile: new PlayerProfile(5, chargebacks, null));

        Assert.Equal(expected, results[0].Status);
    }

    [Fact]
    public void List_rules_read_their_bit_from_the_hit_mask()
    {
        var set = Compile(
            List("blocked-device", RuleType.Blocklist, "blocked-devices", Dimension.Device, 100, RuleAction.Decline),
            List("blocked-card", RuleType.Blocklist, "blocked-cards", Dimension.Card, 100, RuleAction.Decline));

        var (_, results) = Evaluate(set, Card(), listHits: 0b10);

        Assert.Equal(RuleStatus.Passed, results[0].Status);
        Assert.Equal(RuleStatus.Hit, results[1].Status);
    }

    [Fact]
    public void List_rules_do_not_apply_without_the_subject_and_are_skipped_without_lists()
    {
        var set = Compile(List("blocked-card", RuleType.Blocklist, "blocked-cards", Dimension.Card, 100, RuleAction.Decline));

        Assert.Equal(RuleStatus.NotApplicable, Evaluate(set, Wallet(), listHits: ulong.MaxValue).Results[0].Status);
        Assert.Equal(RuleStatus.Skipped, Evaluate(set, Card(), available: SignalAvailability.Velocity).Results[0].Status);
    }

    [Fact]
    public void An_allowlist_hit_lowers_the_score()
    {
        var set = Compile(
            Amount("large", 100m, score: 50),
            List("trusted", RuleType.Allowlist, "trusted-players", Dimension.Player, -30));

        var (summary, _) = Evaluate(set, Card(amount: 200m), listHits: 0b1);

        Assert.Equal(20, summary.Score);
        Assert.Equal(Decision.Approve, summary.Decision);
    }
}
