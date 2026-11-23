using VelocityGuard.Core.Payments;

namespace VelocityGuard.Core.Rules;

/// <summary>
/// A rule after compilation: every parameter resolved to the form the evaluator needs
/// (limits in minor units by currency id, window and list checks as slot numbers).
/// Evaluating it touches no dictionaries, strings or heap memory.
/// </summary>
public readonly record struct CompiledRule
{
    public required string Id { get; init; }

    public required RuleType Type { get; init; }

    public string? Description { get; init; }

    public RuleMode Mode { get; init; }

    public RuleAction Action { get; init; }

    public int Score { get; init; }

    public Dimension Dimension { get; init; }

    /// <summary>Velocity rules: slot in <see cref="VelocityPlan.Windows"/>. List rules: bit in the list-hit mask.</summary>
    public int Slot { get; init; }

    public TimeSpan Window { get; init; }

    public long MaxCount { get; init; }

    public required CurrencyLimits Limits { get; init; }

    public int MaxAccountAgeDays { get; init; }

    public int MinChargebacks { get; init; }

    public string? List { get; init; }

    public bool IsShadow => Mode == RuleMode.Shadow;

    public bool IsVelocity => Type is RuleType.VelocityCount or RuleType.VelocitySum;
}

/// <summary>A distinct (list, dimension) membership check; rules refer to it by bit position.</summary>
public readonly record struct ListCheck(string List, Dimension Dimension);
