using Microsoft.Extensions.Caching.Distributed;
using Polly;

namespace VelocityGuard.Redis.Resilience;

/// <summary>
/// HybridCache's L2 (the Redis <see cref="IDistributedCache"/>) behind the same pipeline as every other Redis call.
/// HybridCache already treats an L2 failure as a miss; this makes that failure arrive within the operation
/// timeout, or immediately while the breaker is open, instead of after the client's own timeout.
/// </summary>
/// <remarks>
/// Deliberately implements only the <c>byte[]</c> API, not <c>IBufferDistributedCache</c>: abandoning a timed-out
/// read must never leave a late writer pointed at a buffer HybridCache has already reused.
/// </remarks>
internal sealed class ResilientDistributedCache(IDistributedCache inner, ResiliencePipeline pipeline) : IDistributedCache
{
    public byte[]? Get(string key) =>
        pipeline.Execute(static s => s.inner.Get(s.key), (inner, key));

    public Task<byte[]?> GetAsync(string key, CancellationToken token = default) =>
        pipeline.ExecuteAsync(
            static async (s, ct) => await s.inner.GetAsync(s.key, ct).WaitAsync(ct).ConfigureAwait(false),
            (inner, key),
            token).AsTask();

    public void Set(string key, byte[] value, DistributedCacheEntryOptions options) =>
        pipeline.Execute(static s => s.inner.Set(s.key, s.value, s.options), (inner, key, value, options));

    public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default) =>
        pipeline.ExecuteAsync(
            static async (s, ct) => await s.inner.SetAsync(s.key, s.value, s.options, ct).WaitAsync(ct).ConfigureAwait(false),
            (inner, key, value, options),
            token).AsTask();

    public void Refresh(string key) =>
        pipeline.Execute(static s => s.inner.Refresh(s.key), (inner, key));

    public Task RefreshAsync(string key, CancellationToken token = default) =>
        pipeline.ExecuteAsync(
            static async (s, ct) => await s.inner.RefreshAsync(s.key, ct).WaitAsync(ct).ConfigureAwait(false),
            (inner, key),
            token).AsTask();

    public void Remove(string key) =>
        pipeline.Execute(static s => s.inner.Remove(s.key), (inner, key));

    public Task RemoveAsync(string key, CancellationToken token = default) =>
        pipeline.ExecuteAsync(
            static async (s, ct) => await s.inner.RemoveAsync(s.key, ct).WaitAsync(ct).ConfigureAwait(false),
            (inner, key),
            token).AsTask();
}
