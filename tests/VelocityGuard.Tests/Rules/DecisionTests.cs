using VelocityGuard.Core.Evaluation;
using VelocityGuard.Core.Payments;
using VelocityGuard.Core.Rules;
using static VelocityGuard.Tests.TestData;

namespace VelocityGuard.Tests.Rules;

public sealed class DecisionTests
{
    [Theory]
    [InlineData(39, Decision.Approve)]
    [InlineData(40, Decision.Review)]
    [InlineData(79, Decision.Review)]
    [InlineData(80, Decision.Decline)]
    public void Score_thresholds_map_to_decisions(int score, Decision expected)
    {
        var set = Compile(Amount("large", 1m, score));

        var (summary, _) = Evaluate(set, Card(amount: 10m));

        Assert.Equal(score, summary.Score);
        Assert.Equal(expected, summary.Decision);
    }

    [Theory]
    [InlineData(RuleAction.Review, Decision.Review)]
    [InlineData(RuleAction.Decline, Decision.Decline)]
    public void A_forcing_action_wins_over_a_low_score(RuleAction action, Decision expected)
    {
        var set = Compile(List("blocked-device", RuleType.Blocklist, "blocked-devices", Dimension.Device, score: 5, action));

        var (summary, _) = Evaluate(set, Card(), listHits: 1);

        Assert.Equal(5, summary.Score);
        Assert.Equal(expected, summary.Decision);
    }

    [Fact]
    public void Shadow_hits_are_counted_but_never_change_the_decision()
    {
        var shadow = Amount("shadow-large", 1m, score: 100);
        shadow.Mode = RuleMode.Shadow;
        shadow.Action = RuleAction.Decline;
        var set = Compile(shadow);

        var (summary, results) = Evaluate(set, Card());

        Assert.Equal(RuleStatus.Hit, results[0].Status);
        Assert.Equal(Decision.Approve, summary.Decision);
        Assert.Equal(0, summary.Score);
        Assert.Equal(0, summary.Hits);
        Assert.Equal(1, summary.ShadowHits);
    }

    [Fact]
    public void Score_is_clamped_to_0_100_but_the_raw_sum_is_kept()
    {
        var high = Compile(Amount("a", 1m, 70), new RuleDefinition { Id = "b", Type = RuleType.CountryMismatch, Score = 60 });
        var low = Compile(List("trusted", RuleType.Allowlist, "trusted-players", Dimension.Player, -40));

        var (highSummary, _) = Evaluate(high, Card(binCountry: "DE", ipCountry: "NL"));
        var (lowSummary, _) = Evaluate(low, Card(), listHits: 1);

        Assert.Equal((100, 130), (highSummary.Score, highSummary.RawScore));
        Assert.Equal((0, -40), (lowSummary.Score, lowSummary.RawScore));
    }

    [Fact]
    public void Results_buffer_must_fit_every_rule()
    {
        var set = Compile(Amount("a", 1m), Amount("b", 2m));
        var attempt = Card();

        Assert.Throws<ArgumentException>(() =>
        {
            var context = new RiskContext(attempt, [], 0, null, SignalAvailability.All);
            set.Evaluate(in context, new RuleResult[1]);
        });
    }

    [Fact]
    public void Evaluation_does_not_allocate()
    {
        var set = Compile(
            VelocityCount("player-10m", Dimension.Player, TimeSpan.FromMinutes(10), 5),
            VelocityCount("device-1m", Dimension.Device, TimeSpan.FromMinutes(1), 3),
            VelocitySum("player-24h", Dimension.Player, TimeSpan.FromDays(1), 2000m),
            Amount("large", 5000m),
            new RuleDefinition { Id = "mismatch", Type = RuleType.CountryMismatch, Score = 25 },
            new RuleDefinition { Id = "first", Type = RuleType.FirstDeposit, Limits = new() { ["EUR"] = 1000m }, Score = 20 },
            List("blocked-device", RuleType.Blocklist, "blocked-devices", Dimension.Device, 100, RuleAction.Decline));
        var attempt = Card(amount: 120m, ipCountry: "NL");
        var windows = new long[set.Velocity.SlotCount];
        var results = new RuleResult[set.Count];
        var profile = Core.Signals.PlayerProfile.New;

        RunOnce(); // warm up: JIT, statics
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1_000; i++)
        {
            RunOnce();
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);

        void RunOnce()
        {
            var context = new RiskContext(attempt, windows, 0, profile, SignalAvailability.All);
            set.Evaluate(in context, results);
        }
    }
}
