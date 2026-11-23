using StackExchange.Redis;
using VelocityGuard.Core.Payments;
using VelocityGuard.Core.Velocity;
using VelocityGuard.Redis;
using VelocityGuard.Tests.Velocity;

namespace VelocityGuard.IntegrationTests;

/// <summary>The shared store contract plus Redis-specific checks: TTLs, hash slots, atomicity under concurrency.</summary>
[Trait("Category", "Integration")]
public sealed class RedisVelocityStoreTests(RedisFixture redis) : VelocityStoreContract
{
    protected override Task<(IVelocityStore Store, IAttemptJournal Journal)> CreateAsync()
    {
        var store = NewStore(out _);
        return Task.FromResult<(IVelocityStore, IAttemptJournal)>((store, store));
    }

    [Fact]
    public async Task Subject_keys_expire_after_the_subjects_longest_window()
    {
        var store = NewStore(out var keys);

        await Record(store, "ttl-1", T0);

        var db = redis.Connection.GetDatabase();
        var deviceTtl = await db.KeyTimeToLiveAsync(keys.Attempts(Dimension.Device, "dev-contract"));
        var playerTtl = await db.KeyTimeToLiveAsync(keys.Amounts(Dimension.Player, "p-contract"));
        Assert.InRange(deviceTtl!.Value, TimeSpan.FromMinutes(9), TimeSpan.FromMinutes(10)); // longest device window: 10m
        Assert.InRange(playerTtl!.Value, TimeSpan.FromHours(23), TimeSpan.FromHours(24));    // player sum window: 24h
        Assert.False(await db.KeyExistsAsync(keys.Amounts(Dimension.Device, "dev-contract"))); // no sum windows for devices
    }

    [Fact]
    public void A_subjects_two_keys_live_in_one_hash_slot_so_the_script_runs_on_a_cluster()
    {
        var keys = new RedisKeys("vg");

        foreach (var dimension in Enum.GetValues<Dimension>())
        {
            Assert.Equal(
                redis.Connection.HashSlot(keys.Attempts(dimension, "subject-1")),
                redis.Connection.HashSlot(keys.Amounts(dimension, "subject-1")));
        }
    }

    [Fact]
    public async Task Concurrent_attempts_from_several_instances_are_each_counted_exactly_once()
    {
        var prefix = RedisFixture.UniquePrefix();
        var instances = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => ConnectionMultiplexer.ConnectAsync(redis.ConnectionString)));
        try
        {
            var stores = instances.Select(c => new RedisVelocityStore(c, new RedisKeys(prefix), TimeSpan.FromDays(1))).ToArray();

            var results = await Task.WhenAll(Enumerable.Range(0, 400).Select(i =>
                Record(stores[i % stores.Length], $"cc-{i}", T0.AddMilliseconds(i % 37), amount: 10m)));

            // The Lua script reads and writes in one atomic step: every caller saw a different count.
            Assert.Equal(Enumerable.Range(1, 400).Select(i => (long)i), results.Select(r => r[3]).Order());

            var last = await Record(stores[0], "cc-last", T0.AddSeconds(1), amount: 10m);
            Assert.Equal(401 * 1_000, last[2]);
        }
        finally
        {
            foreach (var instance in instances)
            {
                await instance.DisposeAsync();
            }
        }
    }

    private RedisVelocityStore NewStore(out RedisKeys keys)
    {
        keys = new RedisKeys(RedisFixture.UniquePrefix());
        return new RedisVelocityStore(redis.Connection, keys, TimeSpan.FromDays(1));
    }
}
