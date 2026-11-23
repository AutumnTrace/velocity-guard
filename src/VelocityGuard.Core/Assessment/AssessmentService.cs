using System.Buffers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VelocityGuard.Core.Diagnostics;
using VelocityGuard.Core.Evaluation;
using VelocityGuard.Core.Payments;
using VelocityGuard.Core.Rules;
using VelocityGuard.Core.Signals;
using VelocityGuard.Core.Velocity;

namespace VelocityGuard.Core.Assessment;

/// <summary>
/// Runs one assessment: cached signals, one velocity round trip, rule evaluation, decision, degraded-mode policy.
/// </summary>
public sealed class AssessmentService(
    RuleSetProvider rules,
    IVelocityStore velocity,
    RiskSignalCache signals,
    IOptionsMonitor<DegradedModeOptions> degradedOptions,
    RiskMetrics metrics,
    TimeProvider time,
    ILogger<AssessmentService> logger)
{
    /// <summary>Rule sets up to this size evaluate into a stack buffer; bigger ones rent from the array pool.</summary>
    private const int StackResultLimit = 128;

    public async ValueTask<AssessmentResult> AssessAsync(PaymentAttempt attempt, CancellationToken cancellationToken)
    {
        var started = time.GetTimestamp();
        var ruleSet = rules.Current;
        using var activity = RiskTelemetry.Source.StartActivity("velocityguard.assess");
        activity?.SetTag("velocityguard.rules.version", ruleSet.Version);

        var available = SignalAvailability.All;
        List<string>? unavailable = null;

        // 1. Cached signals through HybridCache: L1 memory, then L2 Redis, then the system of record.
        PlayerProfile? profile = null;
        if (ruleSet.NeedsProfile)
        {
            try
            {
                profile = await signals.GetProfileAsync(attempt.PlayerId, cancellationToken).ConfigureAwait(false);
            }
            catch (DependencyUnavailableException ex)
            {
                available &= ~SignalAvailability.Profile;
                (unavailable ??= []).Add(ex.Dependency);
                Log.Unavailable(logger, ex, attempt.AttemptId);
            }
        }

        var listHits = 0UL;
        if (!ruleSet.ListChecks.IsEmpty)
        {
            try
            {
                listHits = await signals.CheckListsAsync(ruleSet, attempt, cancellationToken).ConfigureAwait(false);
            }
            catch (DependencyUnavailableException ex)
            {
                available &= ~SignalAvailability.Lists;
                AddOnce(ref unavailable, ex.Dependency);
                Log.Unavailable(logger, ex, attempt.AttemptId);
            }
        }

        // 2. Velocity: record the attempt and read every window in one round trip.
        var now = time.GetUtcNow();
        long[] windows;
        if (ruleSet.Velocity.IsEmpty)
        {
            windows = [];
        }
        else
        {
            try
            {
                windows = await velocity.RecordAsync(new VelocityRequest(attempt, ruleSet.Velocity, now), cancellationToken).ConfigureAwait(false);
            }
            catch (DependencyUnavailableException ex)
            {
                available &= ~SignalAvailability.Velocity;
                AddOnce(ref unavailable, ex.Dependency);
                Log.Unavailable(logger, ex, attempt.AttemptId);
                windows = new long[ruleSet.Velocity.SlotCount];
            }
        }

        // 3. Evaluate and decide (synchronous, no allocation inside the rule loop).
        var result = Decide(ruleSet, attempt, windows, listHits, profile, available, unavailable, started);
        activity?.SetTag("velocityguard.decision", result.Decision.ToString());
        activity?.SetTag("velocityguard.score", result.Score);

        // 4. A declined payment moves no money: take its amount back out of the sum windows.
        if (result.Decision == Decision.Decline && (available & SignalAvailability.Velocity) != 0 && ruleSet.Velocity.HasSums)
        {
            try
            {
                await velocity.ReleaseAmountAsync(AttemptRecord.From(attempt, now), cancellationToken).ConfigureAwait(false);
            }
            catch (DependencyUnavailableException ex)
            {
                Log.ReleaseFailed(logger, attempt.AttemptId, ex.Reason);
            }
        }

        return result;
    }

    private AssessmentResult Decide(
        CompiledRuleSet ruleSet,
        PaymentAttempt attempt,
        long[] windows,
        ulong listHits,
        PlayerProfile? profile,
        SignalAvailability available,
        List<string>? unavailable,
        long started)
    {
        RuleResult[]? rented = null;
        Span<RuleResult> results = ruleSet.Count <= StackResultLimit
            ? stackalloc RuleResult[StackResultLimit]
            : (rented = ArrayPool<RuleResult>.Shared.Rent(ruleSet.Count));

        try
        {
            var evaluationStarted = time.GetTimestamp();
            var context = new RiskContext(attempt, windows, listHits, profile, available);
            var summary = ruleSet.Evaluate(in context, results);
            metrics.Evaluated(time.GetElapsedTime(evaluationStarted));

            var compiled = ruleSet.Rules;
            var reasons = new List<RuleHit>(summary.Hits);
            var shadowHits = summary.ShadowHits == 0 ? [] : new List<RuleHit>(summary.ShadowHits);
            var evaluated = new List<string>(ruleSet.Count);
            List<string>? skipped = null;

            for (var i = 0; i < compiled.Length; i++)
            {
                ref readonly var rule = ref compiled[i];
                var outcome = results[i];
                switch (outcome.Status)
                {
                    case RuleStatus.Skipped:
                        (skipped ??= []).Add(rule.Id);
                        continue;
                    case RuleStatus.NotApplicable:
                        continue;
                }

                evaluated.Add(rule.Id);
                if (!outcome.IsHit)
                {
                    continue;
                }

                metrics.RuleHit(rule.Id, rule.IsShadow);
                var hit = new RuleHit(rule.Id, rule.Type, rule.Score, rule.Action, RuleExplainer.Explain(in rule, outcome, attempt));
                (rule.IsShadow ? shadowHits : reasons).Add(hit);
            }

            var decision = summary.Decision;
            DegradedInfo? degraded = null;
            if (available != SignalAvailability.All && unavailable is { Count: > 0 })
            {
                var policy = degradedOptions.CurrentValue.Policy;
                decision = ApplyPolicy(policy, summary.Decision);
                degraded = new DegradedInfo(policy, unavailable, skipped ?? [], summary.Decision);
                foreach (var dependency in unavailable)
                {
                    metrics.Degraded(policy, dependency);
                }

                Log.AssessedDegraded(logger, attempt.AttemptId, string.Join(", ", unavailable), summary.Decision, policy, decision);
            }

            var elapsed = time.GetElapsedTime(started);
            metrics.AssessmentCompleted(decision, degraded is not null, elapsed);
            Log.Assessed(logger, attempt.AttemptId, decision, summary.Score, summary.Hits, elapsed.TotalMilliseconds);

            return new AssessmentResult(
                attempt.AttemptId,
                decision,
                summary.Score,
                [.. reasons.OrderByDescending(r => r.Score)],
                shadowHits,
                evaluated,
                degraded,
                ruleSet.Version,
                elapsed);
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<RuleResult>.Shared.Return(rented);
            }
        }
    }

    internal static Decision ApplyPolicy(DegradedPolicy policy, Decision rulesDecision) => policy switch
    {
        DegradedPolicy.FailOpen => rulesDecision,
        DegradedPolicy.FailClosed => Decision.Decline,
        _ => rulesDecision > Decision.Review ? rulesDecision : Decision.Review,
    };

    private static void AddOnce(ref List<string>? list, string value)
    {
        list ??= [];
        if (!list.Contains(value))
        {
            list.Add(value);
        }
    }
}
