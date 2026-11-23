using System.Diagnostics.Metrics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Options;
using VelocityGuard.Core.Assessment;
using VelocityGuard.Core.Diagnostics;
using VelocityGuard.Core.Rules;

namespace VelocityGuard.Tests.Assessment;

/// <summary>Hot reload through real configuration and <see cref="IOptionsMonitor{TOptions}"/>.</summary>
public sealed class RuleSetProviderTests : IDisposable
{
    private readonly IConfigurationRoot _configuration;
    private readonly ServiceProvider _services;

    public RuleSetProviderTests()
    {
        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Risk:Decision:ReviewAt"] = "40",
                ["Risk:Decision:DeclineAt"] = "80",
                ["Risk:Rules:0:Id"] = "device-burst",
                ["Risk:Rules:0:Type"] = "VelocityCount",
                ["Risk:Rules:0:Dimension"] = "Device",
                ["Risk:Rules:0:Window"] = "00:01:00",
                ["Risk:Rules:0:MaxCount"] = "3",
                ["Risk:Rules:0:Score"] = "50",
            })
            .Build();

        _services = new ServiceCollection()
            .AddLogging()
            .AddMetrics()
            .AddSingleton(TimeProvider.System)
            .AddSingleton<RiskMetrics>()
            .AddSingleton<RuleSetProvider>()
            .Configure<RiskRulesOptions>(_configuration.GetSection(RiskRulesOptions.SectionName))
            .BuildServiceProvider();
    }

    [Fact]
    public void Swaps_in_a_new_compiled_rule_set_when_configuration_changes()
    {
        var provider = _services.GetRequiredService<RuleSetProvider>();
        var before = provider.Current;
        using var reloads = Collect("velocityguard.rules.reloads");

        _configuration["Risk:Rules:0:MaxCount"] = "5";
        _configuration.Reload();

        var after = provider.Current;
        Assert.NotSame(before, after);
        Assert.NotEqual(before.Version, after.Version);
        Assert.Equal(5, after.Rules[0].MaxCount);
        Assert.Equal(3, before.Rules[0].MaxCount); // in-flight requests keep the instance they started with
        Assert.Equal("applied", Assert.Single(reloads.GetMeasurementSnapshot()).Tags["result"]);
    }

    [Fact]
    public void Keeps_the_last_good_rule_set_when_a_reload_is_invalid()
    {
        var provider = _services.GetRequiredService<RuleSetProvider>();
        var before = provider.Current;
        using var reloads = Collect("velocityguard.rules.reloads");

        _configuration["Risk:Rules:0:MaxCount"] = "0";
        _configuration.Reload();

        Assert.Same(before, provider.Current);
        Assert.Equal("rejected", Assert.Single(reloads.GetMeasurementSnapshot()).Tags["result"]);

        // ...and picks up the next valid edit.
        _configuration["Risk:Rules:0:MaxCount"] = "4";
        _configuration.Reload();
        Assert.Equal(4, provider.Current.Rules[0].MaxCount);
    }

    [Fact]
    public void Ignores_reloads_that_do_not_change_the_content()
    {
        var provider = _services.GetRequiredService<RuleSetProvider>();
        var before = provider.Current;

        _configuration.Reload();

        Assert.Same(before, provider.Current);
    }

    [Fact]
    public void Refuses_to_start_with_an_invalid_rule_set()
    {
        _configuration["Risk:Decision:ReviewAt"] = "90";

        var ex = Assert.Throws<OptionsValidationException>(() => _services.GetRequiredService<RuleSetProvider>());

        Assert.Contains(ex.Failures, f => f.Contains("ReviewAt < DeclineAt"));
    }

    public void Dispose() => _services.Dispose();

    private MetricCollector<long> Collect(string instrument) =>
        new(_services.GetRequiredService<IMeterFactory>(), RiskMetrics.MeterName, instrument);
}
