using VelocityGuard.Core.Payments;

namespace VelocityGuard.Core.Rules;

/// <summary>
/// The rule set as written in configuration (<c>rules.json</c>, section <c>Risk</c>).
/// It is validated and compiled into a <see cref="CompiledRuleSet"/> before it is used.
/// </summary>
public sealed class RiskRulesOptions
{
    public const string SectionName = "Risk";

    public DecisionThresholds Decision { get; set; } = new();

    public List<RuleDefinition> Rules { get; set; } = [];
}

/// <summary>Score thresholds. Scores are clamped to 0..100.</summary>
public sealed class DecisionThresholds
{
    /// <summary>A score at or above this sends the payment to manual review.</summary>
    public int ReviewAt { get; set; } = 40;

    /// <summary>A score at or above this declines the payment.</summary>
    public int DeclineAt { get; set; } = 80;
}

public enum RuleType
{
    Unknown = 0,

    /// <summary>More than <c>MaxCount</c> attempts for one subject inside a sliding window.</summary>
    VelocityCount,

    /// <summary>The amount deposited by one subject inside a sliding window exceeds a per-currency limit.</summary>
    VelocitySum,

    /// <summary>A single amount at or above a per-currency limit.</summary>
    AmountThreshold,

    /// <summary>The card was issued in a different country than the one the IP geolocates to.</summary>
    CountryMismatch,

    /// <summary>A young account (under <c>MaxAccountAgeDays</c>) deposits at least the per-currency limit.</summary>
    NewAccountAmount,

    /// <summary>The player has no approved deposit yet, and this one is at least the per-currency limit (if any).</summary>
    FirstDeposit,

    /// <summary>The player has at least <c>MinChargebacks</c> chargebacks on record.</summary>
    ChargebackHistory,

    /// <summary>The subject is on a deny list.</summary>
    Blocklist,

    /// <summary>The subject is on an allow list; usually carries a negative score.</summary>
    Allowlist,
}

public enum RuleMode
{
    /// <summary>Hits count towards the score and the decision.</summary>
    Active = 0,

    /// <summary>Hits are reported and measured but never change the decision. For trying a rule on live traffic.</summary>
    Shadow = 1,
}

public enum RuleAction
{
    /// <summary>Add the rule's score; the thresholds decide.</summary>
    Score = 0,

    /// <summary>Add the score and force at least a review.</summary>
    Review = 1,

    /// <summary>Add the score and force a decline.</summary>
    Decline = 2,
}

/// <summary>One rule as configured. Which properties apply depends on <see cref="Type"/>.</summary>
public sealed class RuleDefinition
{
    public string Id { get; set; } = string.Empty;

    public RuleType Type { get; set; }

    public string? Description { get; set; }

    public bool Enabled { get; set; } = true;

    public RuleMode Mode { get; set; } = RuleMode.Active;

    public int Score { get; set; }

    public RuleAction Action { get; set; } = RuleAction.Score;

    /// <summary>Velocity and list rules: what the window or list is keyed on.</summary>
    public Dimension? Dimension { get; set; }

    /// <summary>Velocity rules: the sliding window length, e.g. <c>00:10:00</c>.</summary>
    public TimeSpan? Window { get; set; }

    /// <summary><see cref="RuleType.VelocityCount"/>: the largest count that still passes.</summary>
    public int? MaxCount { get; set; }

    /// <summary>Amount rules: limit per ISO 4217 currency, in major units (e.g. <c>"EUR": 500</c>).</summary>
    public Dictionary<string, decimal> Limits { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary><see cref="RuleType.NewAccountAmount"/>: accounts younger than this many days are "new".</summary>
    public int? MaxAccountAgeDays { get; set; }

    /// <summary><see cref="RuleType.ChargebackHistory"/>: how many chargebacks trigger the rule.</summary>
    public int? MinChargebacks { get; set; }

    /// <summary>List rules: the list name, e.g. <c>blocked-devices</c>.</summary>
    public string? List { get; set; }
}
