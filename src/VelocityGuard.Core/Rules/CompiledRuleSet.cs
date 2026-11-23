using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using VelocityGuard.Core.Evaluation;
using VelocityGuard.Core.Payments;

namespace VelocityGuard.Core.Rules;

/// <summary>
/// An immutable, ready-to-run rule set. Built once per configuration change by <see cref="RuleCompiler"/>
/// and then shared by every request until the next reload swaps in a new instance.
/// </summary>
public sealed class CompiledRuleSet
{
    private readonly CompiledRule[] _rules;

    internal CompiledRuleSet(
        string version,
        DateTimeOffset compiledAt,
        int reviewAt,
        int declineAt,
        CompiledRule[] rules,
        VelocityPlan velocity,
        ImmutableArray<ListCheck> listChecks)
    {
        Version = version;
        CompiledAt = compiledAt;
        ReviewAt = reviewAt;
        DeclineAt = declineAt;
        _rules = rules;
        Velocity = velocity;
        ListChecks = listChecks;
        NeedsProfile = rules.Any(r => r.Type is RuleType.FirstDeposit or RuleType.ChargebackHistory);
    }

    /// <summary>Content hash of the source configuration; changes whenever any rule or threshold changes.</summary>
    public string Version { get; }

    public DateTimeOffset CompiledAt { get; }

    public int ReviewAt { get; }

    public int DeclineAt { get; }

    public ReadOnlySpan<CompiledRule> Rules => _rules;

    public int Count => _rules.Length;

    public VelocityPlan Velocity { get; }

    public ImmutableArray<ListCheck> ListChecks { get; }

    /// <summary>True when at least one rule needs the player's profile, so the lookup can be skipped otherwise.</summary>
    public bool NeedsProfile { get; }

    public Decision DecisionFor(int score) =>
        score >= DeclineAt ? Decision.Decline
        : score >= ReviewAt ? Decision.Review
        : Decision.Approve;

    /// <summary>
    /// Evaluates every rule against <paramref name="context"/>, writing one <see cref="RuleResult"/> per rule
    /// into <paramref name="results"/> (which must hold at least <see cref="Count"/> items).
    /// Does not allocate.
    /// </summary>
    public EvaluationSummary Evaluate(scoped in RiskContext context, Span<RuleResult> results)
    {
        if (results.Length < _rules.Length)
        {
            ThrowResultsTooSmall();
        }

        int raw = 0, hits = 0, shadowHits = 0, skipped = 0;
        var forced = Decision.Approve;

        var rules = _rules;
        for (var i = 0; i < rules.Length; i++)
        {
            ref readonly var rule = ref rules[i];
            var result = EvaluateRule(in rule, in context);
            results[i] = result;

            switch (result.Status)
            {
                case RuleStatus.Hit when rule.IsShadow:
                    shadowHits++;
                    break;

                case RuleStatus.Hit:
                    hits++;
                    raw += rule.Score;
                    var actionDecision = (Decision)(int)rule.Action;
                    if (actionDecision > forced)
                    {
                        forced = actionDecision;
                    }

                    break;

                case RuleStatus.Skipped:
                    skipped++;
                    break;
            }
        }

        var score = Math.Clamp(raw, 0, 100);
        var decision = DecisionFor(score);
        if (forced > decision)
        {
            decision = forced;
        }

        return new EvaluationSummary(decision, score, raw, hits, shadowHits, skipped);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static RuleResult EvaluateRule(in CompiledRule rule, scoped in RiskContext ctx)
    {
        var attempt = ctx.Attempt;
        var amount = attempt.Amount;

        switch (rule.Type)
        {
            case RuleType.VelocityCount:
            {
                if (attempt.SubjectFor(rule.Dimension) is null)
                {
                    return default;
                }

                if (!ctx.Has(SignalAvailability.Velocity))
                {
                    return new RuleResult(RuleStatus.Skipped);
                }

                var count = ctx.Windows[rule.Slot];
                return Compare(count > rule.MaxCount, count, rule.MaxCount);
            }

            case RuleType.VelocitySum:
            {
                if (attempt.SubjectFor(rule.Dimension) is null || !rule.Limits.TryGet(amount.Currency, out var limit))
                {
                    return default;
                }

                if (!ctx.Has(SignalAvailability.Velocity))
                {
                    return new RuleResult(RuleStatus.Skipped);
                }

                var sum = ctx.Windows[rule.Slot];
                return Compare(sum > limit, sum, limit);
            }

            case RuleType.AmountThreshold:
            {
                return rule.Limits.TryGet(amount.Currency, out var limit)
                    ? Compare(amount.Minor >= limit, amount.Minor, limit)
                    : default;
            }

            case RuleType.CountryMismatch:
            {
                if (attempt.Method != PaymentMethod.Card || attempt.CardBinCountry is null)
                {
                    return default;
                }

                return Compare(!string.Equals(attempt.CardBinCountry, attempt.IpCountry, StringComparison.Ordinal), 0, 0);
            }

            case RuleType.NewAccountAmount:
            {
                if (!rule.Limits.TryGet(amount.Currency, out var limit))
                {
                    return default;
                }

                var hit = attempt.AccountAgeDays < rule.MaxAccountAgeDays && amount.Minor >= limit;
                return Compare(hit, amount.Minor, limit);
            }

            case RuleType.FirstDeposit:
            {
                var limit = 0L;
                if (!rule.Limits.IsEmpty && !rule.Limits.TryGet(amount.Currency, out limit))
                {
                    return default;
                }

                if (!ctx.Has(SignalAvailability.Profile) || ctx.Profile is not { } profile)
                {
                    return new RuleResult(RuleStatus.Skipped);
                }

                return Compare(profile.ApprovedDeposits == 0 && amount.Minor >= limit, amount.Minor, limit);
            }

            case RuleType.ChargebackHistory:
            {
                if (!ctx.Has(SignalAvailability.Profile) || ctx.Profile is not { } profile)
                {
                    return new RuleResult(RuleStatus.Skipped);
                }

                return Compare(profile.Chargebacks >= rule.MinChargebacks, profile.Chargebacks, rule.MinChargebacks);
            }

            case RuleType.Blocklist:
            case RuleType.Allowlist:
            {
                if (attempt.SubjectFor(rule.Dimension) is null)
                {
                    return default;
                }

                if (!ctx.Has(SignalAvailability.Lists))
                {
                    return new RuleResult(RuleStatus.Skipped);
                }

                return Compare(((ctx.ListHits >> rule.Slot) & 1UL) != 0, 0, 0);
            }

            default:
                return default;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static RuleResult Compare(bool hit, long observed, long limit) =>
        new(hit ? RuleStatus.Hit : RuleStatus.Passed, observed, limit);

    private static void ThrowResultsTooSmall() =>
        throw new ArgumentException("The results buffer must hold one item per rule.", "results");
}
