using VelocityGuard.Core.Evaluation;
using VelocityGuard.Core.Payments;
using VelocityGuard.Core.Rules;
using VelocityGuard.Core.Signals;

namespace VelocityGuard.Tests;

/// <summary>Builders for attempts and rule sets, so each test states only what it is about.</summary>
internal static class TestData
{
    public static PaymentAttempt Card(
        decimal amount = 25m,
        string currency = "EUR",
        string player = "p-1001",
        string? device = "dev-1",
        string ip = "203.0.113.10",
        string ipCountry = "DE",
        string binCountry = "DE",
        string fingerprint = "fp_test_0001",
        int accountAgeDays = 365,
        string attemptId = "att_test")
    {
        PaymentAttemptParser.TryNormalizeIp(ip, out var canonical, out var key);
        return new PaymentAttempt
        {
            AttemptId = attemptId,
            PlayerId = player,
            Amount = Money.FromMajor(amount, Currency.Parse(currency)),
            Method = PaymentMethod.Card,
            IpAddress = canonical!,
            IpKey = key!,
            IpCountry = ipCountry,
            CardBinCountry = binCountry,
            CardFingerprint = fingerprint,
            DeviceId = device,
            AccountAgeDays = accountAgeDays,
        };
    }

    public static PaymentAttempt Wallet(decimal amount = 25m, string currency = "EUR", string player = "p-1001", string? device = "dev-1") =>
        Card(amount, currency, player, device) with
        {
            Method = PaymentMethod.Wallet,
            CardBinCountry = null,
            CardFingerprint = null,
            WalletNetwork = "ewallet-a",
        };

    public static PaymentAttemptInput CardInput() => new()
    {
        AttemptId = "att_1",
        PlayerId = "p-1001",
        Amount = 25.00m,
        Currency = "EUR",
        Method = "card",
        Ip = "203.0.113.10",
        IpCountry = "de",
        CardBinCountry = "de",
        CardFingerprint = "fp_9f2c4e1a",
        DeviceId = "dev-1",
        AccountAgeDays = 120,
    };

    public static RiskRulesOptions Options(params RuleDefinition[] rules) => new()
    {
        Decision = new DecisionThresholds { ReviewAt = 40, DeclineAt = 80 },
        Rules = [.. rules],
    };

    public static CompiledRuleSet Compile(params RuleDefinition[] rules) =>
        RuleCompiler.Compile(Options(rules), DateTimeOffset.UnixEpoch);

    public static (EvaluationSummary Summary, RuleResult[] Results) Evaluate(
        CompiledRuleSet set,
        PaymentAttempt attempt,
        long[]? windows = null,
        ulong listHits = 0,
        PlayerProfile? profile = null,
        SignalAvailability available = SignalAvailability.All)
    {
        var results = new RuleResult[set.Count];
        var context = new RiskContext(attempt, windows ?? new long[set.Velocity.SlotCount], listHits, profile ?? PlayerProfile.New, available);
        var summary = set.Evaluate(in context, results);
        return (summary, results);
    }

    public static RuleDefinition VelocityCount(string id, Dimension dimension, TimeSpan window, int max, int score = 50) => new()
    {
        Id = id,
        Type = RuleType.VelocityCount,
        Dimension = dimension,
        Window = window,
        MaxCount = max,
        Score = score,
    };

    public static RuleDefinition VelocitySum(string id, Dimension dimension, TimeSpan window, decimal eurLimit, int score = 50) => new()
    {
        Id = id,
        Type = RuleType.VelocitySum,
        Dimension = dimension,
        Window = window,
        Limits = new(StringComparer.OrdinalIgnoreCase) { ["EUR"] = eurLimit },
        Score = score,
    };

    public static RuleDefinition Amount(string id, decimal eurLimit, int score = 50) => new()
    {
        Id = id,
        Type = RuleType.AmountThreshold,
        Limits = new(StringComparer.OrdinalIgnoreCase) { ["EUR"] = eurLimit },
        Score = score,
    };

    public static RuleDefinition List(string id, RuleType type, string list, Dimension dimension, int score, RuleAction action = RuleAction.Score) => new()
    {
        Id = id,
        Type = type,
        List = list,
        Dimension = dimension,
        Score = score,
        Action = action,
    };
}
