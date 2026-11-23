using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace VelocityGuard.Core.Rules;

/// <summary>
/// Turns validated <see cref="RiskRulesOptions"/> into a <see cref="CompiledRuleSet"/>: limits are converted
/// to minor units once, velocity windows are de-duplicated into slots, and list checks into bit positions.
/// </summary>
public static class RuleCompiler
{
    private static readonly RiskRulesValidator Validator = new();

    private static readonly JsonSerializerOptions VersionJson = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Validates and compiles. Throws <see cref="OptionsValidationException"/> listing every problem.</summary>
    public static CompiledRuleSet Compile(RiskRulesOptions options, DateTimeOffset compiledAt)
    {
        var validation = Validator.Validate(Options.DefaultName, options);
        if (validation.Failed)
        {
            throw new OptionsValidationException(Options.DefaultName, typeof(RiskRulesOptions), validation.Failures);
        }

        var velocity = new VelocityPlan.Builder();
        var listChecks = new List<ListCheck>();
        var rules = new List<CompiledRule>(options.Rules.Count);

        foreach (var def in options.Rules.Where(r => r.Enabled))
        {
            var slot = 0;
            switch (def.Type)
            {
                case RuleType.VelocityCount:
                    slot = velocity.SlotFor(new WindowSpec(def.Dimension!.Value, def.Window!.Value, WindowAggregate.Count));
                    break;

                case RuleType.VelocitySum:
                    slot = velocity.SlotFor(new WindowSpec(def.Dimension!.Value, def.Window!.Value, WindowAggregate.Sum));
                    break;

                case RuleType.Blocklist:
                case RuleType.Allowlist:
                    var check = new ListCheck(def.List!, def.Dimension!.Value);
                    slot = listChecks.IndexOf(check);
                    if (slot < 0)
                    {
                        listChecks.Add(check);
                        slot = listChecks.Count - 1;
                    }

                    break;
            }

            rules.Add(new CompiledRule
            {
                Id = def.Id,
                Type = def.Type,
                Description = def.Description,
                Mode = def.Mode,
                Action = def.Action,
                Score = def.Score,
                Dimension = def.Dimension ?? default,
                Slot = slot,
                Window = def.Window ?? TimeSpan.Zero,
                MaxCount = def.MaxCount ?? 0,
                Limits = CurrencyLimits.FromMajor(def.Limits),
                MaxAccountAgeDays = def.MaxAccountAgeDays ?? 0,
                MinChargebacks = def.MinChargebacks ?? 0,
                List = def.List,
            });
        }

        return new CompiledRuleSet(
            VersionOf(options),
            compiledAt,
            options.Decision.ReviewAt,
            options.Decision.DeclineAt,
            [.. rules],
            velocity.Build(),
            [.. listChecks]);
    }

    /// <summary>A short content hash of the configuration, so callers can tell which rules produced a decision.</summary>
    public static string VersionOf(RiskRulesOptions options)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(options, VersionJson);
        var hash = SHA256.HashData(json);
        return Convert.ToHexStringLower(hash.AsSpan(0, 6));
    }

    /// <summary>Formats validation failures for a log line.</summary>
    public static string Describe(OptionsValidationException exception) =>
        new StringBuilder().AppendJoin(" | ", exception.Failures).ToString();
}
