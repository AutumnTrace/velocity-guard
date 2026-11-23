using Microsoft.Extensions.Options;
using VelocityGuard.Core.Signals;
using VelocityGuard.Core.Velocity;

namespace VelocityGuard.Api.Hosting;

/// <summary>Adds <c>RiskData:SeedLists</c> entries at startup. Idempotent, and never blocks startup if storage is down.</summary>
internal sealed partial class ListSeeder(
    IRiskDataRepository repository,
    RiskSignalCache cache,
    IOptions<RiskDataOptions> options,
    ILogger<ListSeeder> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var seeded = 0;
        try
        {
            foreach (var (list, values) in options.Value.SeedLists)
            {
                foreach (var value in values)
                {
                    if (await repository.AddToListAsync(list, value, cancellationToken))
                    {
                        await cache.ListEntryChangedAsync(list, value, cancellationToken);
                        seeded++;
                    }
                }
            }

            if (seeded > 0)
            {
                LogSeeded(logger, seeded);
            }
        }
        catch (DependencyUnavailableException ex)
        {
            LogSeedFailed(logger, ex.Dependency, ex.Reason);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(4001, LogLevel.Information, "Seeded {Count} list entries from configuration")]
    private static partial void LogSeeded(ILogger logger, int count);

    [LoggerMessage(4002, LogLevel.Warning, "Skipped list seeding: {Dependency} unavailable ({Reason})")]
    private static partial void LogSeedFailed(ILogger logger, string dependency, string reason);
}
