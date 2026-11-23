using Microsoft.Extensions.Caching.Distributed;
using Polly;
using Polly.CircuitBreaker;

namespace VelocityGuard.Redis.Resilience;

/// <summary>
/// HybridCache's L2 (the Redis <see cref="IDistributedCache"/>) behind the same pipeline as every other Redis call.
/// HybridCache already treats an L2 failure as a miss; this makes that failure arrive within the operation
/// timeout, or immediately while the breaker is open, instead of after the client's own timeout.
/// </summary>
/// <remarks>
/// <para>While the breaker is open, reads return a miss and writes are skipped without throwing: the outage is
/// already reported (breaker events, degraded metrics), and an error log per lookup would only bury it.</para>
/// <para>Deliberately implements only the <c>byte[]</c> API, not <c>IBufferDistributedCache</c>: abandoning a timed-out
/// read must never leave a late writer pointed at a buffer HybridCache has already reused.</para>
/// </remarks>
internal sealed class ResilientDistributedCache(IDistributedCache inner, ResiliencePipeline pipeline) : IDistributedCache
{
    public byte[]? Get(string key)
    {
        try
        {
            return pipeline.Execute(static s => s.inner.Get(s.key), (inner, key));
        }
        catch (BrokenCircuitException)
        {
            return null;
        }
    }

    public async Task<byte[]?> GetAsync(string key, CancellationToken token = default)
    {
        try
        {
            return await pipeline.ExecuteAsync(
                static async (s, ct) => await s.inner.GetAsync(s.key, ct).WaitAsync(ct).ConfigureAwait(false),
                (inner, key),
                token).ConfigureAwait(false);
        }
        catch (BrokenCircuitException)
        {
            return null;
        }
    }

    public void Set(string key, byte[] value, DistributedCacheEntryOptions options)
    {
        try
        {
            pipeline.Execute(static s => s.inner.Set(s.key, s.value, s.options), (inner, key, value, options));
        }
        catch (BrokenCircuitException)
        {
            // Skipped: L1 still holds the value, and L2 is refilled on the next miss once Redis is back.
        }
    }

    public async Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
    {
        try
        {
            await pipeline.ExecuteAsync(
                static async (s, ct) => await s.inner.SetAsync(s.key, s.value, s.options, ct).WaitAsync(ct).ConfigureAwait(false),
                (inner, key, value, options),
                token).ConfigureAwait(false);
        }
        catch (BrokenCircuitException)
        {
            // Skipped, as in Set.
        }
    }

    public void Refresh(string key)
    {
        try
        {
            pipeline.Execute(static s => s.inner.Refresh(s.key), (inner, key));
        }
        catch (BrokenCircuitException)
        {
            // Sliding expiration is not used; nothing to do.
        }
    }

    public async Task RefreshAsync(string key, CancellationToken token = default)
    {
        try
        {
            await pipeline.ExecuteAsync(
                static async (s, ct) => await s.inner.RefreshAsync(s.key, ct).WaitAsync(ct).ConfigureAwait(false),
                (inner, key),
                token).ConfigureAwait(false);
        }
        catch (BrokenCircuitException)
        {
            // As in Refresh.
        }
    }

    public void Remove(string key) =>
        pipeline.Execute(static s => s.inner.Remove(s.key), (inner, key));

    public Task RemoveAsync(string key, CancellationToken token = default) =>
        pipeline.ExecuteAsync(
            static async (s, ct) => await s.inner.RemoveAsync(s.key, ct).WaitAsync(ct).ConfigureAwait(false),
            (inner, key),
            token).AsTask();
}
