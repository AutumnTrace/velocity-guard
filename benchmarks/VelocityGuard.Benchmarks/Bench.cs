using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using VelocityGuard.Api.Hosting;
using VelocityGuard.Core.Payments;
using VelocityGuard.Core.Rules;

namespace VelocityGuard.Benchmarks;

/// <summary>Shared setup: the shipped rules.json, the production service registrations, generated traffic.</summary>
internal static class Bench
{
    public static readonly DateTimeOffset Start = new(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);

    public static IConfiguration Configuration() => new ConfigurationBuilder()
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("rules.json")
        .AddInMemoryCollection(new Dictionary<string, string?> { ["Storage:Provider"] = "InMemory" })
        .Build();

    public static CompiledRuleSet ShippedRules()
    {
        var options = new RiskRulesOptions();
        Configuration().GetSection(RiskRulesOptions.SectionName).Bind(options);
        return RuleCompiler.Compile(options, Start);
    }

    public static ServiceProvider Services(TimeProvider time)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMetrics();
        services.AddVelocityGuard(Configuration());
        services.AddSingleton(time);
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Attempt <paramref name="i"/> of a synthetic stream: unique attempt ids, 5,000 players (each with its own
    /// device and card) and 2,000 IPs, so every subject sees a realistic handful of events per window.
    /// </summary>
    public static PaymentAttempt Attempt(int i, decimal amount = 25m)
    {
        var subject = i % 5_000;
        var input = new PaymentAttemptInput
        {
            AttemptId = $"att_bench_{i}",
            PlayerId = $"p-bench-{subject}",
            Amount = amount,
            Currency = "EUR",
            Method = "card",
            Ip = $"198.51.{i % 2_000 / 250}.{i % 250}",
            IpCountry = "DE",
            CardBinCountry = "DE",
            CardFingerprint = $"fp_bench_{subject:00000}",
            DeviceId = $"dev-bench-{subject}",
            AccountAgeDays = 400,
        };

        return PaymentAttemptParser.TryParse(input, TimeProvider.System, out var attempt, out _)
            ? attempt
            : throw new InvalidOperationException($"Synthetic attempt {i} is invalid.");
    }
}

/// <summary>A clock that moves forward a fixed step every time it is read: 100 ms of simulated traffic per assessment.</summary>
internal sealed class SteppingTimeProvider(DateTimeOffset start, TimeSpan step) : TimeProvider
{
    private long _reads;

    public override DateTimeOffset GetUtcNow() => start + (step * Interlocked.Increment(ref _reads));
}
