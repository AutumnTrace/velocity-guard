using VelocityGuard.Core.Evaluation;
using VelocityGuard.Core.Payments;
using VelocityGuard.Core.Rules;

namespace VelocityGuard.Api.Contracts;

public sealed record RulesResponse(
    string Version,
    DateTimeOffset LoadedAt,
    ThresholdsResponse Decision,
    IReadOnlyList<RuleResponse> Rules)
{
    public static RulesResponse From(CompiledRuleSet set)
    {
        var rules = new List<RuleResponse>(set.Count);
        foreach (ref readonly var rule in set.Rules)
        {
            rules.Add(RuleResponse.From(rule));
        }

        return new RulesResponse(set.Version, set.CompiledAt, new ThresholdsResponse(set.ReviewAt, set.DeclineAt), rules);
    }
}

public sealed record ThresholdsResponse(int ReviewAt, int DeclineAt);

public sealed record RuleResponse(
    string Id,
    RuleType Type,
    RuleMode Mode,
    int Score,
    RuleAction Action,
    string? Description,
    Dimension? Dimension,
    string? Window,
    long? MaxCount,
    IReadOnlyDictionary<string, decimal>? Limits,
    int? MaxAccountAgeDays,
    int? MinChargebacks,
    string? List)
{
    public static RuleResponse From(CompiledRule rule) => new(
        rule.Id,
        rule.Type,
        rule.Mode,
        rule.Score,
        rule.Action,
        rule.Description,
        rule.Type is RuleType.VelocityCount or RuleType.VelocitySum or RuleType.Blocklist or RuleType.Allowlist ? rule.Dimension : null,
        rule.IsVelocity ? RuleExplainer.FormatWindow(rule.Window) : null,
        rule.Type == RuleType.VelocityCount ? rule.MaxCount : null,
        rule.Limits.IsEmpty ? null : rule.Limits.ToMajor(),
        rule.Type == RuleType.NewAccountAmount ? rule.MaxAccountAgeDays : null,
        rule.Type == RuleType.ChargebackHistory ? rule.MinChargebacks : null,
        rule.List);
}

public sealed record ListResponse(string List, int Count, IReadOnlyList<string> Entries);

public sealed record ListEntryResponse(string List, string Value, bool Changed);

public sealed record ReplaceListRequest(IReadOnlyList<string>? Entries);
