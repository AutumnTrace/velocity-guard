using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using VelocityGuard.Core.Assessment;
using VelocityGuard.Core.Diagnostics;
using VelocityGuard.Core.Evaluation;
using VelocityGuard.Core.Payments;
using VelocityGuard.Core.Rules;
using VelocityGuard.Core.Signals;
using VelocityGuard.Core.Velocity;
using static VelocityGuard.Tests.TestData;

namespace VelocityGuard.Tests.Assessment;

public sealed class AssessmentServiceTests
{
    private static readonly RuleDefinition DeviceBurst = VelocityCount("device-burst", Dimension.Device, TimeSpan.FromMinutes(1), max: 3);
    private static readonly RuleDefinition BlockedDevice = List("blocked-device", RuleType.Blocklist, "blocked-devices", Dimension.Device, 100, RuleAction.Decline);

    [Fact]
    public async Task Approves_a_clean_attempt_and_lists_the_rules_it_evaluated()
    {
        using var services = new TestServices(Options(DeviceBurst, Amount("large", 5000m), BlockedDevice));

        var result = await Assess(services, Card(amount: 25m));

        Assert.Equal(Decision.Approve, result.Decision);
        Assert.Equal(0, result.Score);
        Assert.Empty(result.Reasons);
        Assert.Equal(["device-burst", "large", "blocked-device"], result.EvaluatedRules);
        Assert.Null(result.Degraded);
        Assert.Equal(services.Get<RuleSetProvider>().Current.Version, result.RulesVersion);
    }

    [Fact]
    public async Task Velocity_builds_up_across_assessments()
    {
        using var services = new TestServices(Options(DeviceBurst));

        var decisions = new List<Decision>();
        for (var i = 0; i < 4; i++)
        {
            services.Time.Advance(TimeSpan.FromSeconds(5));
            decisions.Add((await Assess(services, Card(attemptId: $"b{i}"))).Decision);
        }

        Assert.Equal([Decision.Approve, Decision.Approve, Decision.Approve, Decision.Review], decisions);
    }

    [Fact]
    public async Task Declined_attempts_do_not_count_towards_sum_windows()
    {
        var dailySum = VelocitySum("player-daily", Dimension.Player, TimeSpan.FromDays(1), eurLimit: 100m);
        using var services = new TestServices(Options(BlockedDevice, dailySum));
        await services.Get<IRiskDataRepository>().AddToListAsync("blocked-devices", "dev-bad", CancellationToken.None);

        var blocked = await Assess(services, Card(amount: 80m, device: "dev-bad", attemptId: "s1"));
        var next = await Assess(services, Card(amount: 80m, device: "dev-good", attemptId: "s2"));

        Assert.Equal(Decision.Decline, blocked.Decision);
        Assert.Equal(Decision.Approve, next.Decision); // 80, not 160: the declined 80 was released
    }

    [Theory]
    [InlineData(DegradedPolicy.FailOpen, Decision.Approve)]
    [InlineData(DegradedPolicy.Review, Decision.Review)]
    [InlineData(DegradedPolicy.FailClosed, Decision.Decline)]
    public async Task Applies_the_degraded_policy_when_the_velocity_store_is_down(DegradedPolicy policy, Decision expected)
    {
        using var services = new TestServices(
            Options(DeviceBurst, Amount("large", 5000m)),
            s => s.AddSingleton<IVelocityStore, UnavailableVelocityStore>(),
            new() { ["Degraded:Policy"] = policy.ToString() });
        using var degradedCounter = new MetricCollector<long>(services.Get<IMeterFactory>(), RiskMetrics.MeterName, "velocityguard.degraded");

        var result = await Assess(services, Card(amount: 25m));

        Assert.Equal(expected, result.Decision);
        Assert.NotNull(result.Degraded);
        Assert.Equal(policy, result.Degraded.Policy);
        Assert.Equal(["velocity-store"], result.Degraded.Unavailable);
        Assert.Equal(["device-burst"], result.Degraded.SkippedRules);
        Assert.Equal(Decision.Approve, result.Degraded.RulesDecision);
        Assert.DoesNotContain("device-burst", result.EvaluatedRules);

        var measurement = Assert.Single(degradedCounter.GetMeasurementSnapshot());
        Assert.Equal("velocity-store", measurement.Tags["dependency"]);
    }

    [Fact]
    public async Task Rules_that_still_have_their_signals_keep_deciding_in_degraded_mode()
    {
        using var services = new TestServices(
            Options(DeviceBurst, BlockedDevice),
            s => s.AddSingleton<IVelocityStore, UnavailableVelocityStore>(),
            new() { ["Degraded:Policy"] = "FailOpen" });
        await services.Get<IRiskDataRepository>().AddToListAsync("blocked-devices", "dev-1", CancellationToken.None);

        var result = await Assess(services, Card(device: "dev-1"));

        Assert.Equal(Decision.Decline, result.Decision);
        Assert.Equal("blocked-device", Assert.Single(result.Reasons).RuleId);
    }

    [Fact]
    public async Task List_and_profile_outages_are_degraded_too()
    {
        CountingRepository? repository = null;
        using var services = new TestServices(
            Options(BlockedDevice, new RuleDefinition { Id = "first", Type = RuleType.FirstDeposit, Score = 10 }),
            s => s.AddSingleton<IRiskDataRepository>(_ => repository = new CountingRepository(new InMemoryRiskDataRepository()) { Down = true }));

        var result = await Assess(services, Card());

        Assert.NotNull(repository);
        Assert.Equal(Decision.Review, result.Decision);
        Assert.Equal(["risk-data"], result.Degraded!.Unavailable);
        Assert.Equal(["blocked-device", "first"], result.Degraded.SkippedRules);
    }

    [Fact]
    public async Task Shadow_hits_are_reported_apart_from_the_reasons()
    {
        var shadow = Amount("shadow-large", 10m, score: 90);
        shadow.Mode = RuleMode.Shadow;
        using var services = new TestServices(Options(shadow, Amount("large", 10m, score: 20)));

        var result = await Assess(services, Card(amount: 50m));

        Assert.Equal(20, result.Score);
        Assert.Equal("large", Assert.Single(result.Reasons).RuleId);
        Assert.Equal("shadow-large", Assert.Single(result.ShadowHits).RuleId);
    }

    [Fact]
    public async Task Reasons_come_back_strongest_first()
    {
        var mismatch = new RuleDefinition { Id = "mismatch", Type = RuleType.CountryMismatch, Score = 25 };
        using var services = new TestServices(Options(mismatch, Amount("large", 10m, score: 45)));

        var result = await Assess(services, Card(amount: 50m, binCountry: "BR", ipCountry: "DE"));

        Assert.Equal(["large", "mismatch"], result.Reasons.Select(r => r.RuleId));
        Assert.Equal((70, Decision.Review), (result.Score, result.Decision));
    }

    private static ValueTask<AssessmentResult> Assess(TestServices services, PaymentAttempt attempt) =>
        services.Get<AssessmentService>().AssessAsync(attempt, CancellationToken.None);
}
