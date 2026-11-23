using System.Diagnostics.Metrics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Options;
using VelocityGuard.Core.Diagnostics;

namespace VelocityGuard.Tests.Api;

/// <summary>Edits a real rules file on disk while the API runs, like an operator would.</summary>
public sealed class RulesHotReloadApiTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string ShippedRules = FindShippedRules();

    private readonly string _directory = Directory.CreateTempSubdirectory("vg-rules-").FullName;
    private readonly string _rulesFile;

    public RulesHotReloadApiTests()
    {
        _rulesFile = Path.Combine(_directory, "rules.json");
        File.Copy(ShippedRules, _rulesFile);
    }

    [Fact]
    public async Task A_saved_edit_to_rules_json_changes_the_next_decision()
    {
        await using var factory = new ApiFactory { RulesFile = _rulesFile };
        var client = factory.CreateClient();
        var before = await client.AssessAsync(ApiClientExtensions.Deposit("p-reload", amount: 600m, binCountry: "BR", accountAgeDays: 2));
        var versionBefore = before.GetProperty("rulesVersion").GetString();

        await EditRulesAsync(rules => rules["Risk"]!["Decision"]!["ReviewAt"] = 60);
        var newVersion = await WaitForNewVersionAsync(client, versionBefore!);
        var after = await client.AssessAsync(ApiClientExtensions.Deposit("p-reload-2", amount: 600m, binCountry: "BR", accountAgeDays: 2));

        Assert.Equal("review", before.Decision());
        Assert.Equal("approve", after.Decision()); // same score (55), higher review threshold
        Assert.Equal(newVersion, after.GetProperty("rulesVersion").GetString());
    }

    [Fact]
    public async Task An_invalid_edit_is_rejected_and_the_old_rules_keep_serving()
    {
        await using var factory = new ApiFactory { RulesFile = _rulesFile };
        var client = factory.CreateClient();
        var versionBefore = (await client.GetFromJsonAsync<JsonElement>("/v1/rules", Ct)).GetProperty("version").GetString();
        using var reloads = new MetricCollector<long>(factory.Services.GetRequiredService<IMeterFactory>(), RiskMetrics.MeterName, "velocityguard.rules.reloads");

        await EditRulesAsync(rules => rules["Risk"]!["Decision"]!["ReviewAt"] = 95); // above DeclineAt
        await reloads.WaitForMeasurementsAsync(1, TimeSpan.FromSeconds(15)).WaitAsync(Ct);

        var versionAfter = (await client.GetFromJsonAsync<JsonElement>("/v1/rules", Ct)).GetProperty("version").GetString();
        Assert.Equal("rejected", reloads.LastMeasurement!.Tags["result"]);
        Assert.Equal(versionBefore, versionAfter);
        Assert.Equal("approve", (await client.AssessAsync(ApiClientExtensions.Deposit("p-reload-3"))).Decision());
    }

    [Fact]
    public void The_service_refuses_to_start_with_invalid_rules()
    {
        File.WriteAllText(_rulesFile, """{ "Risk": { "Decision": { "ReviewAt": 40, "DeclineAt": 80 }, "Rules": [ { "Id": "x", "Type": "VelocityCount" } ] } }""");
        using var factory = new ApiFactory { RulesFile = _rulesFile };

        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        var validation = Assert.IsType<OptionsValidationException>(ex.GetBaseException(), exactMatch: false);
        Assert.Contains(validation.Failures, f => f.Contains("Window is required"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // The file watcher may still hold the directory for a moment; the OS temp cleaner will get it.
        }
    }

    private static string FindShippedRules()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "VelocityGuard.Api", "rules.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("Could not find src/VelocityGuard.Api/rules.json above the test output directory.");
    }

    private async Task EditRulesAsync(Action<JsonNode> edit)
    {
        var rules = JsonNode.Parse(await File.ReadAllTextAsync(_rulesFile))!;
        edit(rules);
        await File.WriteAllTextAsync(_rulesFile, rules.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static async Task<string> WaitForNewVersionAsync(HttpClient client, string oldVersion)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            var version = (await client.GetFromJsonAsync<JsonElement>("/v1/rules", Ct)).GetProperty("version").GetString()!;
            if (version != oldVersion)
            {
                return version;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException("The rules file change was not picked up within 15 seconds.");
    }
}
