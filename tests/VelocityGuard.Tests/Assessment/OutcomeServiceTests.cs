using VelocityGuard.Core.Assessment;
using VelocityGuard.Core.Evaluation;
using VelocityGuard.Core.Payments;
using VelocityGuard.Core.Rules;
using VelocityGuard.Core.Velocity;
using static VelocityGuard.Tests.TestData;

namespace VelocityGuard.Tests.Assessment;

public sealed class OutcomeServiceTests
{
    private static readonly RiskRulesOptions Rules = Options(
        List("blocked-device", RuleType.Blocklist, "blocked-devices", Dimension.Device, 100, RuleAction.Decline),
        List("blocked-card", RuleType.Blocklist, "blocked-cards", Dimension.Card, 100, RuleAction.Decline),
        VelocitySum("player-daily", Dimension.Player, TimeSpan.FromDays(1), eurLimit: 100m),
        new RuleDefinition { Id = "first-deposit", Type = RuleType.FirstDeposit, Score = 10 },
        new RuleDefinition { Id = "chargeback-history", Type = RuleType.ChargebackHistory, MinChargebacks = 1, Score = 60, Action = RuleAction.Review });

    [Fact]
    public async Task An_approved_deposit_ends_the_first_deposit_rule_for_that_player()
    {
        using var services = new TestServices(Rules);
        var first = await Assess(services, Card(attemptId: "o1"));

        var outcome = await Record(services, "o1", PaymentOutcome.Approved);
        var second = await Assess(services, Card(attemptId: "o2"));

        Assert.Contains(first.Reasons, r => r.RuleId == "first-deposit");
        Assert.Equal(OutcomeStatus.Recorded, outcome.Status);
        Assert.Equal(1, outcome.Profile!.ApprovedDeposits);
        Assert.DoesNotContain(second.Reasons, r => r.RuleId == "first-deposit"); // profile cache was invalidated
    }

    [Fact]
    public async Task A_psp_decline_releases_the_amount_from_sum_windows()
    {
        using var services = new TestServices(Rules);
        await Assess(services, Card(amount: 90m, attemptId: "d1"));

        await Record(services, "d1", PaymentOutcome.Declined);
        var next = await Assess(services, Card(amount: 20m, attemptId: "d2"));

        Assert.DoesNotContain(next.Reasons, r => r.RuleId == "player-daily"); // 20 EUR, not 110
    }

    [Fact]
    public async Task A_chargeback_blocks_the_card_and_device_and_flags_the_player()
    {
        using var services = new TestServices(Rules);
        await Assess(services, Card(attemptId: "c1", device: "dev-cb", fingerprint: "fp_cb_000001"));
        await Record(services, "c1", PaymentOutcome.Approved);

        var chargeback = await Record(services, "c1", PaymentOutcome.Chargeback);
        var sameDevice = await Assess(services, Card(attemptId: "c2", player: "p-other", device: "dev-cb", fingerprint: "fp_new_000002"));
        var samePlayer = await Assess(services, Card(attemptId: "c3", device: "dev-new", fingerprint: "fp_new_000003"));

        Assert.Equal(["blocked-cards", "blocked-devices"], chargeback.BlockedOn.Order());
        Assert.Equal(1, chargeback.Profile!.Chargebacks);
        Assert.Equal(Decision.Decline, sameDevice.Decision);
        Assert.Equal(Decision.Review, samePlayer.Decision);
        Assert.Contains(samePlayer.Reasons, r => r.RuleId == "chargeback-history");
    }

    [Fact]
    public async Task Blocking_on_chargeback_can_be_switched_off()
    {
        using var services = new TestServices(Rules, settings: new() { ["Outcomes:BlockOnChargeback"] = "false" });
        await Assess(services, Card(attemptId: "n1"));
        await Record(services, "n1", PaymentOutcome.Approved);

        var chargeback = await Record(services, "n1", PaymentOutcome.Chargeback);

        Assert.Empty(chargeback.BlockedOn);
    }

    [Fact]
    public async Task Reports_unknown_repeated_and_impossible_outcomes()
    {
        using var services = new TestServices(Rules);
        await Assess(services, Card(attemptId: "x1"));

        Assert.Equal(OutcomeStatus.NotFound, (await Record(services, "missing", PaymentOutcome.Approved)).Status);
        Assert.Equal(OutcomeStatus.Recorded, (await Record(services, "x1", PaymentOutcome.Declined)).Status);
        Assert.Equal(OutcomeStatus.AlreadyRecorded, (await Record(services, "x1", PaymentOutcome.Declined)).Status);

        var impossible = await Record(services, "x1", PaymentOutcome.Chargeback);
        Assert.Equal(OutcomeStatus.Conflict, impossible.Status);
        Assert.Equal(PaymentOutcome.Declined, impossible.Previous);
    }

    private static ValueTask<AssessmentResult> Assess(TestServices services, PaymentAttempt attempt) =>
        services.Get<AssessmentService>().AssessAsync(attempt, CancellationToken.None);

    private static ValueTask<OutcomeResult> Record(TestServices services, string attemptId, PaymentOutcome outcome) =>
        services.Get<OutcomeService>().RecordAsync(attemptId, outcome, CancellationToken.None);
}
