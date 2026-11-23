using VelocityGuard.Core.Payments;
using VelocityGuard.Core.Rules;
using static VelocityGuard.Tests.TestData;

namespace VelocityGuard.Tests.Rules;

public sealed class RiskRulesValidatorTests
{
    private readonly RiskRulesValidator _validator = new();

    [Fact]
    public void A_well_formed_rule_set_passes()
    {
        var options = Options(
            VelocityCount("device-burst", Dimension.Device, TimeSpan.FromMinutes(1), 3),
            VelocitySum("player-24h", Dimension.Player, TimeSpan.FromDays(1), 2000m),
            Amount("large", 5000m),
            List("blocked-device", RuleType.Blocklist, "blocked-devices", Dimension.Device, 100, RuleAction.Decline),
            List("trusted", RuleType.Allowlist, "trusted-players", Dimension.Player, -30));

        var result = _validator.Validate(null, options);

        Assert.True(result.Succeeded, result.FailureMessage);
    }

    [Fact]
    public void Reports_every_failure_in_one_pass()
    {
        var options = Options(
            VelocityCount("dup", Dimension.Device, TimeSpan.FromMinutes(1), 3),
            VelocityCount("dup", Dimension.Device, TimeSpan.FromMinutes(1), 3),
            new RuleDefinition { Id = "Bad Id!", Type = RuleType.CountryMismatch },
            new RuleDefinition { Id = "no-type" });

        var result = _validator.Validate(null, options);

        Assert.True(result.Failed);
        var failures = result.Failures!.ToList();
        Assert.Contains(failures, f => f.Contains("'dup'") && f.Contains("more than once"));
        Assert.Contains(failures, f => f.Contains("Id must be"));
        Assert.Contains(failures, f => f.Contains("'no-type'") && f.Contains("Type is missing"));
    }

    public static TheoryData<RuleDefinition, string> InvalidRules => new()
    {
        { new RuleDefinition { Id = "r", Type = RuleType.VelocityCount, Dimension = Dimension.Player, MaxCount = 3 }, "Window is required" },
        { new RuleDefinition { Id = "r", Type = RuleType.VelocityCount, Dimension = Dimension.Player, Window = TimeSpan.FromDays(60), MaxCount = 3 }, "Window is required and must be between" },
        { new RuleDefinition { Id = "r", Type = RuleType.VelocityCount, Window = TimeSpan.FromMinutes(1), MaxCount = 3 }, "Dimension is required" },
        { new RuleDefinition { Id = "r", Type = RuleType.VelocityCount, Dimension = Dimension.Ip, Window = TimeSpan.FromMinutes(1) }, "MaxCount must be at least 1" },
        { new RuleDefinition { Id = "r", Type = RuleType.AmountThreshold }, "Limits needs at least one currency" },
        { new RuleDefinition { Id = "r", Type = RuleType.AmountThreshold, Limits = new() { ["XYZ"] = 10 } }, "'XYZ' is not a supported currency" },
        { new RuleDefinition { Id = "r", Type = RuleType.AmountThreshold, Limits = new() { ["JPY"] = 10.5m } }, "limit for JPY" },
        { new RuleDefinition { Id = "r", Type = RuleType.NewAccountAmount, Limits = new() { ["EUR"] = 10 } }, "MaxAccountAgeDays must be at least 1" },
        { new RuleDefinition { Id = "r", Type = RuleType.ChargebackHistory }, "MinChargebacks must be at least 1" },
        { new RuleDefinition { Id = "r", Type = RuleType.Blocklist, Dimension = Dimension.Device }, "List must be" },
        { new RuleDefinition { Id = "r", Type = RuleType.Allowlist, Dimension = Dimension.Player, List = "vip", Score = 20 }, "allowlist can only lower the score" },
        { new RuleDefinition { Id = "r", Type = RuleType.CountryMismatch, Score = 250 }, "Score must be between -100 and 100" },
    };

    [Theory]
    [MemberData(nameof(InvalidRules))]
    public void Rejects_rules_with_missing_or_out_of_range_parameters(RuleDefinition rule, string expected)
    {
        var result = _validator.Validate(null, Options(rule));

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains(expected));
    }

    [Theory]
    [InlineData(80, 40)]
    [InlineData(50, 50)]
    [InlineData(0, 80)]
    [InlineData(40, 101)]
    public void Thresholds_must_be_ordered_and_within_0_to_100(int reviewAt, int declineAt)
    {
        var options = Options();
        options.Decision = new DecisionThresholds { ReviewAt = reviewAt, DeclineAt = declineAt };

        Assert.True(_validator.Validate(null, options).Failed);
    }

    [Fact]
    public void Caps_distinct_list_checks_at_the_width_of_the_hit_mask()
    {
        var rules = Enumerable.Range(0, RiskRulesValidator.MaxListChecks + 1)
            .Select(i => List($"list-{i}", RuleType.Blocklist, $"list-{i}", Dimension.Device, 10))
            .ToArray();

        var result = _validator.Validate(null, Options(rules));

        Assert.Contains(result.Failures!, f => f.Contains("distinct list/dimension checks"));
    }
}
