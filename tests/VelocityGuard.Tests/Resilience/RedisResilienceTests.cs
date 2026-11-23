using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Polly;
using Polly.Registry;
using StackExchange.Redis;
using VelocityGuard.Core.Velocity;
using VelocityGuard.Redis;
using VelocityGuard.Redis.Resilience;
using static VelocityGuard.Tests.TestData;

namespace VelocityGuard.Tests.Resilience;

/// <summary>The real "redis" pipeline from configuration (circuit breaker around a timeout) wrapped around fake stores.</summary>
public sealed class RedisResilienceTests : IDisposable
{
    private readonly ServiceProvider _services;
    private readonly ResiliencePipeline _pipeline;

    public RedisResilienceTests()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Redis"] = "localhost:1",
                ["Redis:OperationTimeout"] = "00:00:00.100",
                ["Redis:BreakerMinimumThroughput"] = "4",
                ["Redis:BreakerFailureRatio"] = "0.5",
                ["Redis:BreakerBreakDuration"] = "00:00:30",
            })
            .Build();

        _services = new ServiceCollection()
            .AddLogging()
            .AddMetrics()
            .AddRedisStorage(configuration, TimeSpan.FromDays(1))
            .BuildServiceProvider();
        _pipeline = _services.GetRequiredService<ResiliencePipelineProvider<string>>().GetPipeline(RedisStorage.PipelineName);
    }

    [Fact]
    public async Task A_connection_failure_becomes_dependency_unavailable()
    {
        var store = new ResilientVelocityStore<FakeStore>(new FakeStore(() => throw new RedisConnectionException(ConnectionFailureType.UnableToConnect, CommandFlags.None, "down", null, CommandStatus.Unknown)), _pipeline);

        var ex = await Assert.ThrowsAsync<DependencyUnavailableException>(() => Record(store));

        Assert.Equal(("velocity-store", "connection"), (ex.Dependency, ex.Reason));
    }

    [Fact]
    public async Task A_slow_call_is_cut_off_by_the_operation_timeout()
    {
        var store = new ResilientVelocityStore<FakeStore>(new FakeStore(hang: true), _pipeline);
        var started = DateTime.UtcNow;

        var ex = await Assert.ThrowsAsync<DependencyUnavailableException>(() => Record(store));

        Assert.Equal("timeout", ex.Reason);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Repeated_failures_open_the_circuit_and_later_calls_fail_fast()
    {
        var inner = new FakeStore(() => throw new RedisConnectionException(ConnectionFailureType.SocketFailure, CommandFlags.None, "reset", null, CommandStatus.Unknown));
        var store = new ResilientVelocityStore<FakeStore>(inner, _pipeline);

        for (var i = 0; i < 4; i++)
        {
            await Assert.ThrowsAsync<DependencyUnavailableException>(() => Record(store));
        }

        var callsBefore = inner.Calls;
        var ex = await Assert.ThrowsAsync<DependencyUnavailableException>(() => Record(store));

        Assert.Equal("circuit-open", ex.Reason);
        Assert.Equal(callsBefore, inner.Calls); // Redis was not even tried
    }

    [Fact]
    public async Task The_callers_own_cancellation_is_not_reported_as_an_outage()
    {
        var store = new ResilientVelocityStore<FakeStore>(new FakeStore(hang: true), _pipeline);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.RecordAsync(new VelocityRequest(Card(), Compile(VelocityCount("v", Core.Payments.Dimension.Device, TimeSpan.FromMinutes(1), 3)).Velocity, DateTimeOffset.UnixEpoch), cts.Token).AsTask());
    }

    public void Dispose() => _services.Dispose();

    private static Task Record(IVelocityStore store) =>
        store.RecordAsync(new VelocityRequest(Card(), Compile(VelocityCount("v", Core.Payments.Dimension.Device, TimeSpan.FromMinutes(1), 3)).Velocity, DateTimeOffset.UnixEpoch), CancellationToken.None).AsTask();

    private sealed class FakeStore(Func<long[]>? behaviour = null, bool hang = false) : IVelocityStore, IAttemptJournal
    {
        public int Calls { get; private set; }

        public async ValueTask<long[]> RecordAsync(VelocityRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            if (hang)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            return behaviour?.Invoke() ?? [0];
        }

        public ValueTask ReleaseAmountAsync(AttemptRecord attempt, CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask<AttemptRecord?> FindAsync(string attemptId, CancellationToken cancellationToken) => ValueTask.FromResult<AttemptRecord?>(null);

        public ValueTask<OutcomeUpdate> SetOutcomeAsync(string attemptId, PaymentOutcome outcome, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new OutcomeUpdate(OutcomeUpdateStatus.NotFound, null, null));
    }
}
