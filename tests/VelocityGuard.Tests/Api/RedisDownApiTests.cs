using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace VelocityGuard.Tests.Api;

/// <summary>
/// The Redis configuration pointed at a closed port: the real StackExchange.Redis client and the real
/// resilience pipeline fail, and the service has to keep answering under its degraded-mode policy.
/// </summary>
public sealed class RedisDownApiTests(RedisDownApiTests.Factory factory) : IClassFixture<RedisDownApiTests.Factory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Assessments_are_answered_under_the_degraded_policy()
    {
        var result = await _client.AssessAsync(ApiClientExtensions.Deposit("p-redis-down"));

        Assert.Equal("review", result.Decision());
        var degraded = result.GetProperty("degraded");
        Assert.Equal("review", degraded.GetProperty("policy").GetString());
        Assert.Equal("approve", degraded.GetProperty("rulesDecision").GetString());
        Assert.Equal(["risk-data", "velocity-store"], degraded.GetProperty("unavailable").EnumerateArray().Select(e => e.GetString()).Order());
        Assert.Contains("device-burst-1m", degraded.GetProperty("skippedRules").EnumerateArray().Select(e => e.GetString()));
        Assert.True(result.GetProperty("latencyMs").GetDouble() < 1000, "degraded answers must stay fast");
    }

    [Fact]
    public async Task Rules_that_need_no_redis_still_decide()
    {
        var result = await _client.AssessAsync(ApiClientExtensions.Deposit("p-redis-down-2", amount: 6000m));

        Assert.Contains("large-amount", result.ReasonRules());
        Assert.Equal("review", result.Decision());
    }

    [Fact]
    public async Task Readiness_reports_degraded_but_stays_in_rotation()
    {
        var response = await _client.GetAsync("/health/ready", Ct);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Degraded", body.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Writes_that_cannot_be_faked_get_a_503_with_retry_after()
    {
        var response = await _client.PostAsJsonAsync("/v1/assessments/att_any/outcome", new { outcome = "approved" }, Ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.NotNull(response.Headers.RetryAfter);
    }

    public sealed class Factory : ApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Storage:Provider", "Redis");
            builder.UseSetting("ConnectionStrings:Redis", "127.0.0.1:1");
            builder.UseSetting("Redis:ConnectTimeout", "00:00:00.500");
            builder.UseSetting("Degraded:Policy", "Review");
        }
    }
}
