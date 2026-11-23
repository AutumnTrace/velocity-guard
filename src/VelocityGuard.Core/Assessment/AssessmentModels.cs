using VelocityGuard.Core.Evaluation;
using VelocityGuard.Core.Rules;

namespace VelocityGuard.Core.Assessment;

/// <summary>What to decide when a signal (velocity store, lists, profile) cannot be read.</summary>
public enum DegradedPolicy
{
    /// <summary>Decide on the rules that could still run. Keeps conversion up, accepts more risk.</summary>
    FailOpen = 0,

    /// <summary>Decide on the rules that could still run, but never better than review. The default.</summary>
    Review = 1,

    /// <summary>Decline. Safest for the operator, worst for honest players.</summary>
    FailClosed = 2,
}

public sealed class DegradedModeOptions
{
    public const string SectionName = "Degraded";

    public DegradedPolicy Policy { get; set; } = DegradedPolicy.Review;
}

public sealed record AssessmentResult(
    string AttemptId,
    Decision Decision,
    int Score,
    IReadOnlyList<RuleHit> Reasons,
    IReadOnlyList<RuleHit> ShadowHits,
    IReadOnlyList<string> EvaluatedRules,
    DegradedInfo? Degraded,
    string RulesVersion,
    TimeSpan Elapsed);

/// <summary>A rule that matched, with a human-readable explanation.</summary>
public sealed record RuleHit(string RuleId, RuleType Type, int Score, RuleAction Action, string Message);

/// <summary>Present when the decision was made without every signal.</summary>
/// <param name="Policy">The policy that was applied.</param>
/// <param name="Unavailable">Dependencies that could not be read, e.g. <c>velocity-store</c>.</param>
/// <param name="SkippedRules">Rules that could not run because of it.</param>
/// <param name="RulesDecision">What the rules that did run would have decided on their own.</param>
public sealed record DegradedInfo(
    DegradedPolicy Policy,
    IReadOnlyList<string> Unavailable,
    IReadOnlyList<string> SkippedRules,
    Decision RulesDecision);
