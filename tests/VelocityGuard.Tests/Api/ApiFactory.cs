using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace VelocityGuard.Tests.Api;

/// <summary>Boots the real API in memory (in-memory storage, shipped rules.json unless told otherwise, fake clock).</summary>
public class ApiFactory : WebApplicationFactory<Program>
{
    public const string AdminKey = "test-admin-key";

    public FakeTimeProvider Time { get; } = new(TestServices.Start);

    /// <summary>Use this rules file instead of the shipped one.</summary>
    public string? RulesFile { get; init; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("Serilog:MinimumLevel:Default", "Warning");
        builder.UseSetting("Serilog:MinimumLevel:Override:VelocityGuard", "Warning");
        builder.UseSetting("Security:AdminApiKey", AdminKey);
        if (RulesFile is not null)
        {
            builder.UseSetting("Risk:RulesFile", RulesFile);
        }

        builder.ConfigureTestServices(services => services.AddSingleton<TimeProvider>(Time));
    }
}

internal static class ApiClientExtensions
{
    public static async Task<JsonElement> AssessAsync(this HttpClient client, object request)
    {
        var response = await client.PostAsJsonAsync("/v1/assessments", request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    public static string Decision(this JsonElement assessment) => assessment.GetProperty("decision").GetString()!;

    public static string[] ReasonRules(this JsonElement assessment) =>
        [.. assessment.GetProperty("reasons").EnumerateArray().Select(r => r.GetProperty("rule").GetString()!)];

    /// <summary>A routine card deposit from an established player; override what a test is about.</summary>
    public static Dictionary<string, object?> Deposit(
        string player,
        decimal amount = 25.00m,
        string device = "dev-routine",
        string fingerprint = "fp_routine_01",
        string binCountry = "DE",
        string ipCountry = "DE",
        int accountAgeDays = 420,
        string? attemptId = null) => new()
    {
        ["attemptId"] = attemptId,
        ["playerId"] = player,
        ["amount"] = amount,
        ["currency"] = "EUR",
        ["method"] = "card",
        ["ip"] = "203.0.113.10",
        ["ipCountry"] = ipCountry,
        ["card"] = new { binCountry, fingerprint },
        ["deviceId"] = device,
        ["accountAgeDays"] = accountAgeDays,
    };
}
