using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using VelocityGuard.Core.Signals;
using VelocityGuard.Redis;
using VelocityGuard.Tests;
using static VelocityGuard.Tests.TestData;

namespace VelocityGuard.IntegrationTests;

/// <summary>HybridCache with Redis as L2, across several service instances ("nodes") sharing one Redis.</summary>
[Trait("Category", "Integration")]
public sealed class HybridCacheRedisTests(RedisFixture redis)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _prefix = RedisFixture.UniquePrefix();

    [Fact]
    public async Task A_second_instance_is_served_from_the_shared_L2()
    {
        using var a = new Node(redis, _prefix);
        using var b = new Node(redis, _prefix);

        await a.Cache.GetProfileAsync("p-l2", Ct);
        await redis.WaitForKeyAsync($"{_prefix}:cache:{RiskSignalCache.ProfileKey("p-l2")}");
        await b.Cache.GetProfileAsync("p-l2", Ct);

        Assert.Equal(1, a.Repository.ProfileReads);
        Assert.Equal(0, b.Repository.ProfileReads); // L1 miss on b, L2 hit
    }

    [Fact]
    public async Task Tag_invalidation_reaches_instances_that_read_L2_afterwards()
    {
        using var a = new Node(redis, _prefix);
        Assert.False(await a.Cache.IsListedAsync("vip", "p-tag", Ct));
        await redis.WaitForKeyAsync($"{_prefix}:cache:{RiskSignalCache.ListKey("vip", "p-tag")}");

        await a.Repository.ReplaceListAsync("vip", ["p-tag"], Ct);
        await a.Cache.ListReplacedAsync("vip", Ct);

        // c starts after the change: the stale "false" is still in L2, but its tag was invalidated after it was written.
        using var c = new Node(redis, _prefix);
        Assert.True(await c.Cache.IsListedAsync("vip", "p-tag", Ct));
        Assert.Equal(1, c.Repository.ListReads);
        Assert.True(await a.Cache.IsListedAsync("vip", "p-tag", Ct));
    }

    [Fact]
    public async Task Evicting_a_profile_removes_it_from_L2_for_every_instance()
    {
        using var a = new Node(redis, _prefix);
        await a.Cache.GetProfileAsync("p-evict", Ct);
        var l2Key = $"{_prefix}:cache:{RiskSignalCache.ProfileKey("p-evict")}";
        await redis.WaitForKeyAsync(l2Key);

        await a.Cache.ProfileChangedAsync("p-evict", Ct);

        Assert.False(await redis.Connection.GetDatabase().KeyExistsAsync(l2Key));
    }

    /// <summary>One service instance: its own HybridCache L1, the shared Redis L2 and repository, real time.</summary>
    private sealed class Node : IDisposable
    {
        private readonly TestServices _services;

        public Node(RedisFixture redis, string prefix)
        {
            _services = new TestServices(
                Options(Amount("large", 5000m)),
                services =>
                {
                    services.AddSingleton(TimeProvider.System);
                    services.AddSingleton<IRiskDataRepository>(sp => Repository = new CountingRepository(
                        new RedisRiskDataRepository(sp.GetRequiredService<IConnectionMultiplexer>(), sp.GetRequiredService<RedisKeys>())));
                },
                new()
                {
                    ["Storage:Provider"] = "Redis",
                    ["ConnectionStrings:Redis"] = redis.ConnectionString,
                    ["Redis:KeyPrefix"] = prefix,
                });
            Cache = _services.Get<RiskSignalCache>();
        }

        public RiskSignalCache Cache { get; }

        public CountingRepository Repository { get; private set; } = null!;

        public void Dispose() => _services.Dispose();
    }
}
