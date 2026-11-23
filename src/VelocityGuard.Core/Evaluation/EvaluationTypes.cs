using VelocityGuard.Core.Payments;
using VelocityGuard.Core.Signals;

namespace VelocityGuard.Core.Evaluation;

public enum Decision
{
    Approve = 0,
    Review = 1,
    Decline = 2,
}

public enum RuleStatus : byte
{
    /// <summary>The rule does not apply to this attempt (no device id, no limit for the currency, not a card, ...).</summary>
    NotApplicable = 0,

    /// <summary>Evaluated, condition not met.</summary>
    Passed = 1,

    /// <summary>Evaluated, condition met.</summary>
    Hit = 2,

    /// <summary>Not evaluated because a signal it needs was unavailable (degraded mode).</summary>
    Skipped = 3,
}

/// <summary>Which external signals made it into this evaluation.</summary>
[Flags]
public enum SignalAvailability
{
    None = 0,
    Velocity = 1,
    Lists = 2,
    Profile = 4,
    All = Velocity | Lists | Profile,
}

/// <summary>Per-rule outcome. <see cref="Observed"/> and <see cref="Limit"/> are counts or minor units, depending on the rule.</summary>
public readonly record struct RuleResult(RuleStatus Status, long Observed = 0, long Limit = 0)
{
    public bool IsHit => Status == RuleStatus.Hit;

    public bool WasEvaluated => Status is RuleStatus.Passed or RuleStatus.Hit;
}

/// <summary>The aggregate result of evaluating a rule set.</summary>
public readonly record struct EvaluationSummary(
    Decision Decision,
    int Score,
    int RawScore,
    int Hits,
    int ShadowHits,
    int Skipped);

/// <summary>
/// Everything a rule may look at, gathered before evaluation. A <c>ref struct</c>, so the window
/// values can be a span over a pooled or stack buffer and nothing is copied to the heap.
/// </summary>
public readonly ref struct RiskContext
{
    public RiskContext(
        PaymentAttempt attempt,
        ReadOnlySpan<long> windows,
        ulong listHits,
        PlayerProfile? profile,
        SignalAvailability available)
    {
        Attempt = attempt;
        Windows = windows;
        ListHits = listHits;
        Profile = profile;
        Available = available;
    }

    public PaymentAttempt Attempt { get; }

    /// <summary>Velocity slot values, indexed like <see cref="Rules.VelocityPlan.Windows"/>.</summary>
    public ReadOnlySpan<long> Windows { get; }

    /// <summary>Bit <c>i</c> is set when list check <c>i</c> matched.</summary>
    public ulong ListHits { get; }

    public PlayerProfile? Profile { get; }

    public SignalAvailability Available { get; }

    public bool Has(SignalAvailability signal) => (Available & signal) == signal;
}
