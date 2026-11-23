using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Options;
using VelocityGuard.Core.Diagnostics;
using VelocityGuard.Core.Payments;
using VelocityGuard.Core.Rules;

namespace VelocityGuard.Core.Signals;

public sealed class SignalCacheOptions
{
    public const string SectionName = "Cache";

    /// <summary>How long an entry lives in the distributed (L2, Redis) cache.</summary>
    public TimeSpan Expiration { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long an entry lives in each instance's memory (L1). This bounds how stale another instance can be
    /// after a change, because HybridCache has no backplane to push invalidations to other nodes' L1.
    /// </summary>
    public TimeSpan LocalExpiration { get; set; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// Read-through cache for player profiles and list memberships on top of <see cref="HybridCache"/>
/// (L1 in-process memory, L2 Redis when configured), with stampede protection from HybridCache.
/// </summary>
/// <remarks>
/// <para>Single-entry changes (one list entry, one profile) evict exactly that key, which also removes it from L2.</para>
/// <para>Bulk changes (replacing a whole list) invalidate by tag: every cached membership answer for that list,
/// positive or negative, becomes stale at once without enumerating keys.</para>
/// </remarks>
public sealed class RiskSignalCache
{
    private const string ProfileCacheName = "profile";
    private const string ListCacheName = "list";
    private static readonly string[] ProfileTags = ["vg:profiles"];

    private readonly HybridCache _cache;
    private readonly IRiskDataRepository _repository;
    private readonly RiskMetrics _metrics;
    private readonly HybridCacheEntryOptions _entryOptions;
    private readonly ConcurrentDictionary<string, string[]> _listTags = new(StringComparer.Ordinal);

    public RiskSignalCache(HybridCache cache, IRiskDataRepository repository, IOptions<SignalCacheOptions> options, RiskMetrics metrics)
    {
        _cache = cache;
        _repository = repository;
        _metrics = metrics;
        _entryOptions = new HybridCacheEntryOptions
        {
            Expiration = options.Value.Expiration,
            LocalCacheExpiration = options.Value.LocalExpiration,
        };
    }

    public ValueTask<PlayerProfile> GetProfileAsync(string playerId, CancellationToken cancellationToken)
    {
        _metrics.CacheLookup(ProfileCacheName);
        return _cache.GetOrCreateAsync(
            ProfileKey(playerId),
            (Cache: this, PlayerId: playerId),
            static (state, ct) => state.Cache.LoadProfileAsync(state.PlayerId, ct),
            _entryOptions,
            ProfileTags,
            cancellationToken);
    }

    public ValueTask<bool> IsListedAsync(string list, string value, CancellationToken cancellationToken)
    {
        _metrics.CacheLookup(ListCacheName);
        return _cache.GetOrCreateAsync(
            ListKey(list, value),
            (Cache: this, List: list, Value: value),
            static (state, ct) => state.Cache.LoadListedAsync(state.List, state.Value, ct),
            _entryOptions,
            _listTags.GetOrAdd(list, static l => [ListTag(l)]),
            cancellationToken);
    }

    /// <summary>Runs every list check of the rule set for this attempt and returns the hit mask (bit i = check i matched).</summary>
    public async ValueTask<ulong> CheckListsAsync(CompiledRuleSet ruleSet, PaymentAttempt attempt, CancellationToken cancellationToken)
    {
        var hits = 0UL;
        var checks = ruleSet.ListChecks;
        for (var i = 0; i < checks.Length; i++)
        {
            if (attempt.SubjectFor(checks[i].Dimension) is { } value
                && await IsListedAsync(checks[i].List, value, cancellationToken).ConfigureAwait(false))
            {
                hits |= 1UL << i;
            }
        }

        return hits;
    }

    public ValueTask ProfileChangedAsync(string playerId, CancellationToken cancellationToken) =>
        _cache.RemoveAsync(ProfileKey(playerId), cancellationToken);

    public ValueTask ListEntryChangedAsync(string list, string value, CancellationToken cancellationToken) =>
        _cache.RemoveAsync(ListKey(list, value), cancellationToken);

    public ValueTask ListReplacedAsync(string list, CancellationToken cancellationToken) =>
        _cache.RemoveByTagAsync(ListTag(list), cancellationToken);

    public static string ProfileKey(string playerId) => "vg:profile:" + playerId;

    public static string ListKey(string list, string value) => string.Concat("vg:list:", list, ":", value);

    public static string ListTag(string list) => "vg:list:" + list;

    private ValueTask<PlayerProfile> LoadProfileAsync(string playerId, CancellationToken cancellationToken)
    {
        _metrics.CacheMiss(ProfileCacheName);
        return _repository.GetProfileAsync(playerId, cancellationToken);
    }

    private ValueTask<bool> LoadListedAsync(string list, string value, CancellationToken cancellationToken)
    {
        _metrics.CacheMiss(ListCacheName);
        return _repository.IsListedAsync(list, value, cancellationToken);
    }
}
