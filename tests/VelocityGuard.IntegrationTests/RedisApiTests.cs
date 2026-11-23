using System.Net.Http.Json;
using System.Text.Json;
using Testcontainers.Redis;
using VelocityGuard.Tests.Api;

namespace VelocityGuard.IntegrationTests;

/// <summary>The full API on Redis storage, with two instances sharing state the way two pods would.</summary>
[Trait("Category", "Integration")]
public sealed class RedisApiTests(RedisFixture redis) : IAsyncDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _prefix = RedisFixture.UniquePrefix();
    private readonly List<RedisApiFactory> _factories = [];

    [Fact]
    public async Task Two_instances_share_velocity_windows_through_redis()
    {
        var a = Client();
        var b = Client();

        var decisions = new List<string>();
        for (var i = 0; i < 4; i++)
        {
            var client = i % 2 == 0 ? a : b;
            var result = await client.AssessAsync(ApiClientExtensions.Deposit($"p-shared-{i}", device: "dev-shared", fingerprint: $"fp_shared_{i:00}"));
            decisions.Add(result.Decision());
        }

        Assert.Equal(["approve", "approve", "approve", "review"], decisions);
    }

    [Fact]
    public async Task A_chargeback_reported_to_one_instance_blocks_the_device_on_another()
    {
        var a = Client();
        var b = Client();
        await a.AssessAsync(ApiClientExtensions.Deposit("p-redis-cb", device: "dev-redis-cb", fingerprint: "fp_redis_cb1", attemptId: "att_redis_cb"));

        (await a.PostAsJsonAsync("/v1/assessments/att_redis_cb/outcome", new { outcome = "approved" }, Ct)).EnsureSuccessStatusCode();
        (await a.PostAsJsonAsync("/v1/assessments/att_redis_cb/outcome", new { outcome = "chargeback" }, Ct)).EnsureSuccessStatusCode();
        var onB = await b.AssessAsync(ApiClientExtensions.Deposit("p-redis-other", device: "dev-redis-cb", fingerprint: "fp_redis_cb2"));

        Assert.Equal("decline", onB.Decision());
        var profile = await redis.Connection.GetDatabase().HashGetAllAsync($"{_prefix}:profile:p-redis-cb");
        Assert.Equal("1", profile.Single(e => e.Name == "approved").Value.ToString());
        Assert.Equal("1", profile.Single(e => e.Name == "chargebacks").Value.ToString());
    }

    [Fact]
    public async Task Readiness_includes_a_healthy_redis_ping()
    {
        var ready = await Client().GetFromJsonAsync<JsonElement>("/health/ready", Ct);

        Assert.Equal("Healthy", ready.GetProperty("status").GetString());
        Assert.Contains(ready.GetProperty("checks").EnumerateArray(), c => c.GetProperty("name").GetString() == "redis");
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var factory in _factories)
        {
            await factory.DisposeAsync();
        }
    }

    private HttpClient Client()
    {
        var factory = new RedisApiFactory(redis.ConnectionString, _prefix);
        _factories.Add(factory);
        return factory.CreateClient();
    }
}

/// <summary>Freezes Redis mid-run (docker pause): requests must degrade quickly, then recover on their own.</summary>
[Trait("Category", "Integration")]
public sealed class RedisOutageTests : IAsyncLifetime
{
    private readonly RedisContainer _container = new RedisBuilder(RedisFixture.Image).Build();

    public async ValueTask InitializeAsync() => await _container.StartAsync();

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    [Fact]
    public async Task Assessments_degrade_while_redis_hangs_and_recover_afterwards()
    {
        await using var factory = new RedisApiFactory(_container.GetConnectionString(), RedisFixture.UniquePrefix(), new Dictionary<string, string>
        {
            ["Redis:BreakerMinimumThroughput"] = "2",
            ["Redis:BreakerBreakDuration"] = "00:00:01",
            ["Degraded:Policy"] = "Review",
        });
        var client = factory.CreateClient();

        var before = await client.AssessAsync(ApiClientExtensions.Deposit("p-outage-1", device: "dev-outage-1"));
        Assert.False(before.TryGetProperty("degraded", out _));

        await _container.PauseAsync(TestContext.Current.CancellationToken);
        JsonElement during;
        try
        {
            during = await client.AssessAsync(ApiClientExtensions.Deposit("p-outage-2", device: "dev-outage-2"));
        }
        finally
        {
            await _container.UnpauseAsync(TestContext.Current.CancellationToken);
        }

        Assert.Equal("review", during.Decision());
        Assert.Contains("velocity-store", during.GetProperty("degraded").GetProperty("unavailable").EnumerateArray().Select(e => e.GetString()));
        Assert.True(during.GetProperty("latencyMs").GetDouble() < 2_000, "a hung Redis must not hang the assessment");

        // Once Redis answers again, the breaker's half-open probe closes it and answers are whole again.
        var deadline = DateTime.UtcNow.AddSeconds(30);
        JsonElement after;
        do
        {
            await Task.Delay(500, TestContext.Current.CancellationToken);
            after = await client.AssessAsync(ApiClientExtensions.Deposit("p-outage-3", device: "dev-outage-3"));
        }
        while (after.TryGetProperty("degraded", out _) && DateTime.UtcNow < deadline);

        Assert.False(after.TryGetProperty("degraded", out _), "the service did not recover after Redis came back");
    }
}
