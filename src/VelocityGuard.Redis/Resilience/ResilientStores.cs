using Polly;
using Polly.CircuitBreaker;
using Polly.Timeout;
using StackExchange.Redis;
using VelocityGuard.Core.Signals;
using VelocityGuard.Core.Velocity;

namespace VelocityGuard.Redis.Resilience;

/// <summary>Maps Redis and Polly failures to the short reasons reported in degraded responses and metrics.</summary>
public static class RedisFailures
{
    public static string? Classify(Exception exception) => exception switch
    {
        BrokenCircuitException => "circuit-open",
        TimeoutRejectedException or RedisTimeoutException or TimeoutException => "timeout",
        RedisConnectionException => "connection",
        RedisException => "error",
        _ => null,
    };

    /// <summary>Failures that count against the circuit breaker.</summary>
    public static PredicateBuilder<object> Handled() => new PredicateBuilder()
        .Handle<RedisException>()
        .Handle<TimeoutRejectedException>()
        .Handle<TimeoutException>();
}

/// <summary>
/// Runs every call to the inner store through the shared Redis pipeline (circuit breaker around a short timeout)
/// and turns failures into <see cref="DependencyUnavailableException"/>, which the assessment service answers
/// with the configured degraded-mode policy. A caller's own cancellation is passed through untouched.
/// </summary>
public abstract class ResilientDecorator(ResiliencePipeline pipeline, string dependency)
{
    protected async ValueTask<T> RunAsync<T, TState>(Func<TState, CancellationToken, ValueTask<T>> operation, TState state, CancellationToken cancellationToken)
    {
        try
        {
            return await pipeline.ExecuteAsync(operation, state, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && RedisFailures.Classify(ex) is { } reason)
        {
            throw new DependencyUnavailableException(dependency, reason, ex);
        }
    }

    protected async ValueTask RunAsync<TState>(Func<TState, CancellationToken, ValueTask> operation, TState state, CancellationToken cancellationToken)
    {
        try
        {
            await pipeline.ExecuteAsync(operation, state, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && RedisFailures.Classify(ex) is { } reason)
        {
            throw new DependencyUnavailableException(dependency, reason, ex);
        }
    }
}

public sealed class ResilientVelocityStore<TStore>(TStore inner, ResiliencePipeline pipeline)
    : ResilientDecorator(pipeline, "velocity-store"), IVelocityStore, IAttemptJournal
    where TStore : IVelocityStore, IAttemptJournal
{
    public ValueTask<long[]> RecordAsync(VelocityRequest request, CancellationToken cancellationToken) =>
        RunAsync(static (s, ct) => s.inner.RecordAsync(s.request, ct), (inner, request), cancellationToken);

    public ValueTask ReleaseAmountAsync(AttemptRecord attempt, CancellationToken cancellationToken) =>
        RunAsync(static (s, ct) => s.inner.ReleaseAmountAsync(s.attempt, ct), (inner, attempt), cancellationToken);

    public ValueTask<AttemptRecord?> FindAsync(string attemptId, CancellationToken cancellationToken) =>
        RunAsync(static (s, ct) => s.inner.FindAsync(s.attemptId, ct), (inner, attemptId), cancellationToken);

    public ValueTask<OutcomeUpdate> SetOutcomeAsync(string attemptId, PaymentOutcome outcome, CancellationToken cancellationToken) =>
        RunAsync(static (s, ct) => s.inner.SetOutcomeAsync(s.attemptId, s.outcome, ct), (inner, attemptId, outcome), cancellationToken);
}

public sealed class ResilientRiskDataRepository(IRiskDataRepository inner, ResiliencePipeline pipeline)
    : ResilientDecorator(pipeline, "risk-data"), IRiskDataRepository
{
    public ValueTask<PlayerProfile> GetProfileAsync(string playerId, CancellationToken cancellationToken) =>
        RunAsync(static (s, ct) => s.inner.GetProfileAsync(s.playerId, ct), (inner, playerId), cancellationToken);

    public ValueTask<PlayerProfile> ApplyOutcomeAsync(string playerId, PaymentOutcome outcome, DateTimeOffset at, CancellationToken cancellationToken) =>
        RunAsync(static (s, ct) => s.inner.ApplyOutcomeAsync(s.playerId, s.outcome, s.at, ct), (inner, playerId, outcome, at), cancellationToken);

    public ValueTask<bool> IsListedAsync(string list, string value, CancellationToken cancellationToken) =>
        RunAsync(static (s, ct) => s.inner.IsListedAsync(s.list, s.value, ct), (inner, list, value), cancellationToken);

    public ValueTask<bool> AddToListAsync(string list, string value, CancellationToken cancellationToken) =>
        RunAsync(static (s, ct) => s.inner.AddToListAsync(s.list, s.value, ct), (inner, list, value), cancellationToken);

    public ValueTask<bool> RemoveFromListAsync(string list, string value, CancellationToken cancellationToken) =>
        RunAsync(static (s, ct) => s.inner.RemoveFromListAsync(s.list, s.value, ct), (inner, list, value), cancellationToken);

    public ValueTask ReplaceListAsync(string list, IReadOnlyCollection<string> values, CancellationToken cancellationToken) =>
        RunAsync(static (s, ct) => s.inner.ReplaceListAsync(s.list, s.values, ct), (inner, list, values), cancellationToken);

    public ValueTask<IReadOnlyList<string>> GetListAsync(string list, int limit, CancellationToken cancellationToken) =>
        RunAsync(static (s, ct) => s.inner.GetListAsync(s.list, s.limit, ct), (inner, list, limit), cancellationToken);
}
