using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using Testcontainers.Redis;
using VelocityGuard.Tests.Api;

[assembly: AssemblyFixture(typeof(VelocityGuard.IntegrationTests.RedisFixture))]

namespace VelocityGuard.IntegrationTests;

/// <summary>One real Redis for the whole run. Tests isolate themselves with a unique key prefix.</summary>
public sealed class RedisFixture : IAsyncLifetime
{
    public const string Image = "redis:8.10-alpine";

    private readonly RedisContainer _container = new RedisBuilder(Image).Build();

    public string ConnectionString => _container.GetConnectionString();

    public IConnectionMultiplexer Connection { get; private set; } = null!;

    public static string UniquePrefix() => "it" + Guid.NewGuid().ToString("N")[..10];

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        Connection = await ConnectionMultiplexer.ConnectAsync(ConnectionString);
    }

    public async ValueTask DisposeAsync()
    {
        Connection?.Dispose();
        await _container.DisposeAsync();
    }

    /// <summary>Waits for a key that is written in the background (HybridCache writes L2 after returning the value).</summary>
    public async Task WaitForKeyAsync(string key, bool exists = true)
    {
        var db = Connection.GetDatabase();
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (await db.KeyExistsAsync(key) != exists)
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Key {key} did not {(exists ? "appear" : "disappear")} within 5 seconds.");
            }

            await Task.Delay(20);
        }
    }
}

/// <summary>The API on Redis storage, on the real clock.</summary>
public sealed class RedisApiFactory(string connectionString, string prefix, IReadOnlyDictionary<string, string>? settings = null) : ApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Storage:Provider", "Redis");
        builder.UseSetting("ConnectionStrings:Redis", connectionString);
        builder.UseSetting("Redis:KeyPrefix", prefix);
        foreach (var (key, value) in settings ?? new Dictionary<string, string>())
        {
            builder.UseSetting(key, value);
        }

        builder.ConfigureTestServices(services => services.AddSingleton(TimeProvider.System));
    }
}
