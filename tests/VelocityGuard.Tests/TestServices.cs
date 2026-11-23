using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using VelocityGuard.Api.Hosting;
using VelocityGuard.Core.Rules;
using VelocityGuard.Core.Signals;
using VelocityGuard.Core.Velocity;

namespace VelocityGuard.Tests;

/// <summary>
/// The production service registrations (in-memory storage) with a fake clock and a fixed rule set,
/// for testing the engine without HTTP.
/// </summary>
internal sealed class TestServices : IDisposable
{
    public static readonly DateTimeOffset Start = new(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);

    private readonly ServiceProvider _provider;

    public TestServices(RiskRulesOptions rules, Action<IServiceCollection>? configure = null, Dictionary<string, string?>? settings = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings ?? [])
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMetrics();
        services.AddVelocityGuard(configuration);
        services.AddSingleton<IOptionsMonitor<RiskRulesOptions>>(new TestOptionsMonitor<RiskRulesOptions>(rules));
        services.AddSingleton<TimeProvider>(Time);
        configure?.Invoke(services);
        _provider = services.BuildServiceProvider();
    }

    public FakeTimeProvider Time { get; } = new(Start);

    public T Get<T>() where T : notnull => _provider.GetRequiredService<T>();

    public void Dispose() => _provider.Dispose();
}

internal sealed class TestOptionsMonitor<T>(T value) : IOptionsMonitor<T>
{
    private event Action<T, string?>? Changed;

    public T CurrentValue { get; private set; } = value;

    public T Get(string? name) => CurrentValue;

    public IDisposable OnChange(Action<T, string?> listener)
    {
        Changed += listener;
        return new Subscription(() => Changed -= listener);
    }

    public void Set(T value)
    {
        CurrentValue = value;
        Changed?.Invoke(value, Options.DefaultName);
    }

    private sealed class Subscription(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}

/// <summary>A velocity store whose backend is down.</summary>
internal sealed class UnavailableVelocityStore : IVelocityStore
{
    public ValueTask<long[]> RecordAsync(VelocityRequest request, CancellationToken cancellationToken) =>
        throw new DependencyUnavailableException("velocity-store", "timeout");

    public ValueTask ReleaseAmountAsync(AttemptRecord attempt, CancellationToken cancellationToken) =>
        throw new DependencyUnavailableException("velocity-store", "timeout");
}

/// <summary>Wraps a repository, counts reads, and can be switched off to simulate an outage.</summary>
internal sealed class CountingRepository(IRiskDataRepository inner) : IRiskDataRepository
{
    public int ProfileReads { get; private set; }

    public int ListReads { get; private set; }

    public bool Down { get; set; }

    public ValueTask<PlayerProfile> GetProfileAsync(string playerId, CancellationToken cancellationToken)
    {
        ThrowIfDown();
        ProfileReads++;
        return inner.GetProfileAsync(playerId, cancellationToken);
    }

    public ValueTask<bool> IsListedAsync(string list, string value, CancellationToken cancellationToken)
    {
        ThrowIfDown();
        ListReads++;
        return inner.IsListedAsync(list, value, cancellationToken);
    }

    public ValueTask<PlayerProfile> ApplyOutcomeAsync(string playerId, PaymentOutcome outcome, DateTimeOffset at, CancellationToken cancellationToken) =>
        inner.ApplyOutcomeAsync(playerId, outcome, at, cancellationToken);

    public ValueTask<bool> AddToListAsync(string list, string value, CancellationToken cancellationToken) =>
        inner.AddToListAsync(list, value, cancellationToken);

    public ValueTask<bool> RemoveFromListAsync(string list, string value, CancellationToken cancellationToken) =>
        inner.RemoveFromListAsync(list, value, cancellationToken);

    public ValueTask ReplaceListAsync(string list, IReadOnlyCollection<string> values, CancellationToken cancellationToken) =>
        inner.ReplaceListAsync(list, values, cancellationToken);

    public ValueTask<IReadOnlyList<string>> GetListAsync(string list, int limit, CancellationToken cancellationToken) =>
        inner.GetListAsync(list, limit, cancellationToken);

    private void ThrowIfDown()
    {
        if (Down)
        {
            throw new DependencyUnavailableException("risk-data", "connection");
        }
    }
}
