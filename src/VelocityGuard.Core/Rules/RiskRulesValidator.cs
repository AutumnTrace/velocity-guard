using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using VelocityGuard.Core.Payments;

namespace VelocityGuard.Core.Rules;

/// <summary>
/// Checks a rule set before it is compiled. Every problem is reported, not just the first,
/// so a bad edit to <c>rules.json</c> can be fixed in one go.
/// </summary>
public sealed partial class RiskRulesValidator : IValidateOptions<RiskRulesOptions>
{
    public const int MaxRules = 256;

    /// <summary>List checks are tracked in a 64-bit mask on the hot path.</summary>
    public const int MaxListChecks = 64;

    public static readonly TimeSpan MinWindow = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan MaxWindow = TimeSpan.FromDays(31);

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,63}$")]
    private static partial Regex NamePattern();

    public ValidateOptionsResult Validate(string? name, RiskRulesOptions options)
    {
        var failures = new List<string>();

        var decision = options.Decision;
        if (decision.ReviewAt < 1 || decision.DeclineAt > 100 || decision.ReviewAt >= decision.DeclineAt)
        {
            failures.Add($"Decision: need 1 <= ReviewAt < DeclineAt <= 100 (got ReviewAt={decision.ReviewAt}, DeclineAt={decision.DeclineAt}).");
        }

        if (options.Rules.Count > MaxRules)
        {
            failures.Add($"Rules: at most {MaxRules} rules are supported (got {options.Rules.Count}).");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var listChecks = new HashSet<(string, Dimension)>();
        for (var i = 0; i < options.Rules.Count; i++)
        {
            var rule = options.Rules[i];
            var label = string.IsNullOrEmpty(rule.Id) ? $"Rules[{i}]" : $"Rule '{rule.Id}'";

            if (!NamePattern().IsMatch(rule.Id))
            {
                failures.Add($"{label}: Id must be 1-64 lower-case letters, digits or '-'.");
            }
            else if (!ids.Add(rule.Id))
            {
                failures.Add($"{label}: Id is used more than once.");
            }

            if (rule.Score is < -100 or > 100)
            {
                failures.Add($"{label}: Score must be between -100 and 100.");
            }

            ValidateType(rule, label, failures);

            if (rule.Enabled && rule.Type is RuleType.Blocklist or RuleType.Allowlist && rule.List is not null && rule.Dimension is { } d)
            {
                listChecks.Add((rule.List, d));
            }
        }

        if (listChecks.Count > MaxListChecks)
        {
            failures.Add($"Rules: at most {MaxListChecks} distinct list/dimension checks are supported (got {listChecks.Count}).");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static void ValidateType(RuleDefinition rule, string label, List<string> failures)
    {
        switch (rule.Type)
        {
            case RuleType.VelocityCount:
                RequireDimension(rule, label, failures);
                RequireWindow(rule, label, failures);
                if (rule.MaxCount is not >= 1)
                {
                    failures.Add($"{label}: MaxCount must be at least 1.");
                }

                break;

            case RuleType.VelocitySum:
                RequireDimension(rule, label, failures);
                RequireWindow(rule, label, failures);
                RequireLimits(rule, label, failures, required: true);
                break;

            case RuleType.AmountThreshold:
                RequireLimits(rule, label, failures, required: true);
                break;

            case RuleType.CountryMismatch:
                break;

            case RuleType.NewAccountAmount:
                RequireLimits(rule, label, failures, required: true);
                if (rule.MaxAccountAgeDays is not >= 1)
                {
                    failures.Add($"{label}: MaxAccountAgeDays must be at least 1.");
                }

                break;

            case RuleType.FirstDeposit:
                RequireLimits(rule, label, failures, required: false);
                break;

            case RuleType.ChargebackHistory:
                if (rule.MinChargebacks is not >= 1)
                {
                    failures.Add($"{label}: MinChargebacks must be at least 1.");
                }

                break;

            case RuleType.Blocklist:
            case RuleType.Allowlist:
                RequireDimension(rule, label, failures);
                if (rule.List is null || !NamePattern().IsMatch(rule.List))
                {
                    failures.Add($"{label}: List must be 1-64 lower-case letters, digits or '-'.");
                }

                if (rule.Type == RuleType.Allowlist && (rule.Score > 0 || rule.Action != RuleAction.Score))
                {
                    failures.Add($"{label}: an allowlist can only lower the score (Score <= 0, Action = Score).");
                }

                break;

            default:
                failures.Add($"{label}: Type is missing or unknown. Known types: {string.Join(", ", Enum.GetNames<RuleType>().Skip(1))}.");
                break;
        }
    }

    private static void RequireDimension(RuleDefinition rule, string label, List<string> failures)
    {
        if (rule.Dimension is not { } d || !Enum.IsDefined(d))
        {
            failures.Add($"{label}: Dimension is required ({string.Join(", ", Enum.GetNames<Dimension>())}).");
        }
    }

    private static void RequireWindow(RuleDefinition rule, string label, List<string> failures)
    {
        if (rule.Window is not { } w || w < MinWindow || w > MaxWindow)
        {
            failures.Add($"{label}: Window is required and must be between {MinWindow} and {MaxWindow.TotalDays} days.");
        }
        else if (w.Ticks % TimeSpan.TicksPerMillisecond != 0)
        {
            failures.Add($"{label}: Window must be a whole number of milliseconds.");
        }
    }

    private static void RequireLimits(RuleDefinition rule, string label, List<string> failures, bool required)
    {
        if (required && rule.Limits.Count == 0)
        {
            failures.Add($"{label}: Limits needs at least one currency, e.g. {{ \"EUR\": 500 }}.");
        }

        foreach (var (code, amount) in rule.Limits)
        {
            if (!Currency.TryParse(code, out var currency))
            {
                failures.Add($"{label}: '{code}' is not a supported currency.");
            }
            else if (!Money.TryFromMajor(amount, currency, out _, out var error))
            {
                failures.Add($"{label}: limit for {currency.Code} {error}.");
            }
        }
    }
}
