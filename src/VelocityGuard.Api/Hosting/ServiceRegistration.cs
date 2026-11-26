using Microsoft.Extensions.DependencyInjection.Extensions;
using VelocityGuard.Core.Assessment;
using VelocityGuard.Core.Diagnostics;
using VelocityGuard.Core.Rules;
using VelocityGuard.Core.Signals;
using VelocityGuard.Core.Velocity;
using VelocityGuard.Redis;

namespace VelocityGuard.Api.Hosting;

public enum StorageProvider
{
    /// <summary>Everything in process memory. For local runs and tests; nothing is shared between instances.</summary>
    InMemory = 0,

    /// <summary>Velocity windows, journal, lists and profiles in Redis; HybridCache L2 in Redis too.</summary>
    Redis = 1,
}

public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    public StorageProvider Provider { get; set; } = StorageProvider.InMemory;

    /// <summary>How long assessed attempts are kept so outcomes (and chargebacks) can be matched to them.</summary>
    public TimeSpan JournalRetention { get; set; } = TimeSpan.FromDays(30);
}

public sealed class RiskDataOptions
{
    public const string SectionName = "RiskData";

    /// <summary>Entries added to lists at startup (idempotent). Handy for demos; real lists come from the admin API.</summary>
    public Dictionary<string, string[]> SeedLists { get; set; } = new(StringComparer.Ordinal);
}

public static class ServiceRegistration
{
    public static IServiceCollection AddVelocityGuard(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton(TimeProvider.System);

        // Rules are validated by the RuleSetProvider itself: strict at startup, forgiving on reload.
        services.AddOptions<RiskRulesOptions>().Bind(configuration.GetSection(RiskRulesOptions.SectionName));

        services.AddOptions<DegradedModeOptions>()
            .Bind(configuration.GetSection(DegradedModeOptions.SectionName))
            .Validate(o => Enum.IsDefined(o.Policy), "Degraded:Policy must be FailOpen, Review or FailClosed.")
            .ValidateOnStart();

        services.AddOptions<SignalCacheOptions>()
            .Bind(configuration.GetSection(SignalCacheOptions.SectionName))
            .Validate(o => o.LocalExpiration > TimeSpan.Zero && o.LocalExpiration <= o.Expiration, "Cache: need 0 < LocalExpiration <= Expiration.")
            .ValidateOnStart();

        // List names here must follow the same rule as rules and the admin API. Otherwise a chargeback
        // block or a seed would land in a list no rule reads and the admin API cannot clear.
        services.AddOptions<OutcomeOptions>()
            .Bind(configuration.GetSection(OutcomeOptions.SectionName))
            .Validate(o => o.ChargebackBlocklists.Values.All(ListNames.IsValidName), $"Outcomes:ChargebackBlocklists: list names must be {ListNames.NameRule}.")
            .ValidateOnStart();

        services.AddOptions<RiskDataOptions>()
            .Bind(configuration.GetSection(RiskDataOptions.SectionName))
            .Validate(o => o.SeedLists.Keys.All(ListNames.IsValidName), $"RiskData:SeedLists: list names must be {ListNames.NameRule}.")
            .Validate(o => o.SeedLists.Values.All(entries => entries.All(ListNames.IsValidEntry)), $"RiskData:SeedLists: entries must be {ListNames.EntryRule}.")
            .ValidateOnStart();
        services.AddOptions<SecurityOptions>().Bind(configuration.GetSection(SecurityOptions.SectionName));

        services.AddOptions<StorageOptions>()
            .Bind(configuration.GetSection(StorageOptions.SectionName))
            .Validate(o => o.JournalRetention >= TimeSpan.FromHours(1), "Storage:JournalRetention must be at least one hour.")
            .ValidateOnStart();

        services.AddHybridCache(options => options.MaximumKeyLength = 512);

        services.AddSingleton<RiskMetrics>();
        services.AddSingleton<RuleSetProvider>();
        services.AddSingleton<RiskSignalCache>();
        services.AddSingleton<AssessmentService>();
        services.AddSingleton<OutcomeService>();
        services.AddHostedService<ListSeeder>();
        services.AddExceptionHandler<DependencyUnavailableHandler>();

        var storage = configuration.GetSection(StorageOptions.SectionName).Get<StorageOptions>() ?? new StorageOptions();
        switch (storage.Provider)
        {
            case StorageProvider.InMemory:
                services.AddSingleton(_ => new InMemoryVelocityStore(storage.JournalRetention));
                services.AddSingleton<IVelocityStore>(sp => sp.GetRequiredService<InMemoryVelocityStore>());
                services.AddSingleton<IAttemptJournal>(sp => sp.GetRequiredService<InMemoryVelocityStore>());
                services.AddSingleton<IRiskDataRepository, InMemoryRiskDataRepository>();
                break;

            case StorageProvider.Redis:
                services.AddRedisStorage(configuration, storage.JournalRetention);
                break;

            default:
                throw new InvalidOperationException($"Unknown storage provider '{storage.Provider}'.");
        }

        return services;
    }
}
