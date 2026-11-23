using System.Diagnostics;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Polly;
using Polly.CircuitBreaker;
using Polly.Registry;
using StackExchange.Redis;
using VelocityGuard.Core.Signals;
using VelocityGuard.Core.Velocity;
using VelocityGuard.Redis.Resilience;

namespace VelocityGuard.Redis;

public sealed class RedisOptions
{
    public const string SectionName = "Redis";

    /// <summary>StackExchange.Redis configuration string. Falls back to <c>ConnectionStrings:Redis</c>.</summary>
    public string? Configuration { get; set; }

    public string KeyPrefix { get; set; } = "vg";

    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>The client's own command timeout. Also bounds HybridCache L2 reads, which Polly does not wrap.</summary>
    public TimeSpan ClientTimeout { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Per-operation timeout applied by the resilience pipeline. Redis normally answers in well under a millisecond.</summary>
    public TimeSpan OperationTimeout { get; set; } = TimeSpan.FromMilliseconds(100);

    public double BreakerFailureRatio { get; set; } = 0.5;

    public int BreakerMinimumThroughput { get; set; } = 20;

    public TimeSpan BreakerSamplingDuration { get; set; } = TimeSpan.FromSeconds(10);

    public TimeSpan BreakerBreakDuration { get; set; } = TimeSpan.FromSeconds(15);
}

public static class RedisStorage
{
    /// <summary>One pipeline, so one circuit breaker, for every Redis call: when Redis is down, everything fails fast together.</summary>
    public const string PipelineName = "redis";

    public static IServiceCollection AddRedisStorage(this IServiceCollection services, IConfiguration configuration, TimeSpan journalRetention)
    {
        services.AddOptions<RedisOptions>()
            .Bind(configuration.GetSection(RedisOptions.SectionName))
            .PostConfigure(o => o.Configuration ??= configuration.GetConnectionString("Redis"))
            .Validate(o => !string.IsNullOrWhiteSpace(o.Configuration), "Set ConnectionStrings:Redis (or Redis:Configuration) when Storage:Provider is Redis.")
            .Validate(o => o.OperationTimeout > TimeSpan.Zero && o.OperationTimeout <= o.ClientTimeout, "Redis: need 0 < OperationTimeout <= ClientTimeout.")
            .Validate(o => o.BreakerFailureRatio is > 0 and <= 1 && o.BreakerMinimumThroughput >= 2, "Redis: BreakerFailureRatio must be in (0, 1] and BreakerMinimumThroughput at least 2.")
            .Validate(o => o.BreakerSamplingDuration >= TimeSpan.FromMilliseconds(500) && o.BreakerBreakDuration >= TimeSpan.FromMilliseconds(500), "Redis: breaker durations must be at least 500 ms.")
            .ValidateOnStart();

        services.AddSingleton<IConnectionMultiplexer>(sp => Connect(sp.GetRequiredService<IOptions<RedisOptions>>().Value));
        services.AddSingleton(sp => new RedisKeys(sp.GetRequiredService<IOptions<RedisOptions>>().Value.KeyPrefix));

        services.AddResilienceEnricher();
        services.AddResiliencePipeline(PipelineName, (builder, context) =>
        {
            var options = context.ServiceProvider.GetRequiredService<IOptions<RedisOptions>>().Value;
            builder
                .AddCircuitBreaker(new CircuitBreakerStrategyOptions
                {
                    FailureRatio = options.BreakerFailureRatio,
                    MinimumThroughput = options.BreakerMinimumThroughput,
                    SamplingDuration = options.BreakerSamplingDuration,
                    BreakDuration = options.BreakerBreakDuration,
                    ShouldHandle = RedisFailures.Handled(),
                })
                .AddTimeout(options.OperationTimeout);
        });

        services.AddSingleton(sp => new RedisVelocityStore(sp.GetRequiredService<IConnectionMultiplexer>(), sp.GetRequiredService<RedisKeys>(), journalRetention));
        services.AddSingleton(sp => new ResilientVelocityStore<RedisVelocityStore>(sp.GetRequiredService<RedisVelocityStore>(), Pipeline(sp)));
        services.AddSingleton<IVelocityStore>(sp => sp.GetRequiredService<ResilientVelocityStore<RedisVelocityStore>>());
        services.AddSingleton<IAttemptJournal>(sp => sp.GetRequiredService<ResilientVelocityStore<RedisVelocityStore>>());
        services.AddSingleton<IRiskDataRepository>(sp => new ResilientRiskDataRepository(
            new RedisRiskDataRepository(sp.GetRequiredService<IConnectionMultiplexer>(), sp.GetRequiredService<RedisKeys>()),
            Pipeline(sp)));

        // HybridCache picks up IDistributedCache as its L2: share the multiplexer, keep cache keys apart,
        // and send L2 calls through the same breaker so a hung Redis costs one operation timeout at most.
        services.AddOptions<RedisCacheOptions>().Configure<IServiceProvider>((cache, sp) =>
        {
            cache.InstanceName = sp.GetRequiredService<IOptions<RedisOptions>>().Value.KeyPrefix + ":cache:";
            cache.ConnectionMultiplexerFactory = () => Task.FromResult(sp.GetRequiredService<IConnectionMultiplexer>());
        });
        services.AddSingleton<IDistributedCache>(sp => new ResilientDistributedCache(
            new RedisCache(sp.GetRequiredService<IOptions<RedisCacheOptions>>()),
            Pipeline(sp)));

        services.AddHealthChecks().AddCheck<RedisHealthCheck>("redis", tags: ["ready"]);
        return services;
    }

    private static ResiliencePipeline Pipeline(IServiceProvider services) =>
        services.GetRequiredService<ResiliencePipelineProvider<string>>().GetPipeline(PipelineName);

    private static ConnectionMultiplexer Connect(RedisOptions options)
    {
        var config = ConfigurationOptions.Parse(options.Configuration!);
        config.AbortOnConnectFail = false;              // start even if Redis is down; degraded mode covers it
        config.BacklogPolicy = BacklogPolicy.FailFast;  // while disconnected, fail commands now instead of queueing them
        config.ConnectTimeout = (int)options.ConnectTimeout.TotalMilliseconds;
        config.SyncTimeout = (int)options.ClientTimeout.TotalMilliseconds;
        config.AsyncTimeout = (int)options.ClientTimeout.TotalMilliseconds;
        config.ClientName ??= "velocity-guard";
        return ConnectionMultiplexer.Connect(config);
    }
}

/// <summary>
/// Pings Redis. Reports <see cref="HealthStatus.Degraded"/> (still HTTP 200), not Unhealthy, when it is down:
/// the service keeps answering under its degraded-mode policy, and pulling every instance out of the load
/// balancer at once would turn a Redis blip into a full outage for callers.
/// </summary>
internal sealed class RedisHealthCheck(IConnectionMultiplexer redis) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var started = Stopwatch.GetTimestamp();
            await redis.GetDatabase().PingAsync().WaitAsync(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            return HealthCheckResult.Healthy($"PING in {Stopwatch.GetElapsedTime(started).TotalMilliseconds:0.00} ms");
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            return HealthCheckResult.Degraded("Redis unreachable; assessments follow the degraded-mode policy", ex);
        }
    }
}
