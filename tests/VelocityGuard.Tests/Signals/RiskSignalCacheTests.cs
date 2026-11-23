using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using VelocityGuard.Core.Assessment;
using VelocityGuard.Core.Diagnostics;
using VelocityGuard.Core.Payments;
using VelocityGuard.Core.Rules;
using VelocityGuard.Core.Signals;
using VelocityGuard.Core.Velocity;
using static VelocityGuard.Tests.TestData;

namespace VelocityGuard.Tests.Signals;

/// <summary>HybridCache behaviour with the in-process L1 only; the Redis L2 path is covered by the integration tests.</summary>
public sealed class RiskSignalCacheTests : IDisposable
{
    private readonly CountingRepository _repository = new(new InMemoryRiskDataRepository());
    private readonly TestServices _services;
    private readonly RiskSignalCache _cache;

    public RiskSignalCacheTests()
    {
        _services = new TestServices(
            Options(Amount("large", 5000m)),
            s => s.AddSingleton<IRiskDataRepository>(_repository));
        _cache = _services.Get<RiskSignalCache>();
    }

    [Fact]
    public async Task Profiles_are_read_from_the_repository_once_and_then_served_from_memory()
    {
        var meters = _services.Get<IMeterFactory>();
        using var lookups = new MetricCollector<long>(meters, RiskMetrics.MeterName, "velocityguard.cache.lookups");
        using var misses = new MetricCollector<long>(meters, RiskMetrics.MeterName, "velocityguard.cache.misses");

        var first = await _cache.GetProfileAsync("p-1", CancellationToken.None);
        var second = await _cache.GetProfileAsync("p-1", CancellationToken.None);

        Assert.Equal(1, _repository.ProfileReads);
        Assert.Same(first, second); // immutable type: L1 hands out the same instance, no deserialisation
        Assert.Equal(2, lookups.GetMeasurementSnapshot().Count);
        Assert.Single(misses.GetMeasurementSnapshot());
    }

    [Fact]
    public async Task A_profile_change_evicts_the_cached_profile()
    {
        await _cache.GetProfileAsync("p-2", CancellationToken.None);
        await _repository.ApplyOutcomeAsync("p-2", PaymentOutcome.Approved, TestServices.Start, CancellationToken.None);

        var stale = await _cache.GetProfileAsync("p-2", CancellationToken.None);
        await _cache.ProfileChangedAsync("p-2", CancellationToken.None);
        var fresh = await _cache.GetProfileAsync("p-2", CancellationToken.None);

        Assert.Equal(0, stale.ApprovedDeposits);
        Assert.Equal(1, fresh.ApprovedDeposits);
    }

    [Fact]
    public async Task Replacing_a_list_invalidates_every_cached_answer_for_it_by_tag()
    {
        // Cache two negative answers and one for an unrelated list.
        Assert.False(await _cache.IsListedAsync("blocked-devices", "dev-a", CancellationToken.None));
        Assert.False(await _cache.IsListedAsync("blocked-devices", "dev-b", CancellationToken.None));
        Assert.False(await _cache.IsListedAsync("blocked-ips", "192.0.2.1", CancellationToken.None));
        var readsBefore = _repository.ListReads;

        await _repository.ReplaceListAsync("blocked-devices", ["dev-a", "dev-b"], CancellationToken.None);
        await _cache.ListReplacedAsync("blocked-devices", CancellationToken.None);

        Assert.True(await _cache.IsListedAsync("blocked-devices", "dev-a", CancellationToken.None));
        Assert.True(await _cache.IsListedAsync("blocked-devices", "dev-b", CancellationToken.None));
        Assert.False(await _cache.IsListedAsync("blocked-ips", "192.0.2.1", CancellationToken.None));
        Assert.Equal(readsBefore + 2, _repository.ListReads); // the other list stayed cached
    }

    [Fact]
    public async Task A_single_entry_change_evicts_only_that_entry()
    {
        Assert.False(await _cache.IsListedAsync("blocked-cards", "fp_one_0001", CancellationToken.None));
        Assert.False(await _cache.IsListedAsync("blocked-cards", "fp_two_0002", CancellationToken.None));
        var readsBefore = _repository.ListReads;

        await _repository.AddToListAsync("blocked-cards", "fp_one_0001", CancellationToken.None);
        await _cache.ListEntryChangedAsync("blocked-cards", "fp_one_0001", CancellationToken.None);

        Assert.True(await _cache.IsListedAsync("blocked-cards", "fp_one_0001", CancellationToken.None));
        Assert.False(await _cache.IsListedAsync("blocked-cards", "fp_two_0002", CancellationToken.None));
        Assert.Equal(readsBefore + 1, _repository.ListReads);
    }

    [Fact]
    public async Task List_checks_produce_a_hit_mask_in_rule_set_order()
    {
        var set = Compile(
            List("blocked-device", RuleType.Blocklist, "blocked-devices", Dimension.Device, 100),
            List("blocked-card", RuleType.Blocklist, "blocked-cards", Dimension.Card, 100),
            List("trusted", RuleType.Allowlist, "trusted-players", Dimension.Player, -30));
        await _repository.AddToListAsync("blocked-cards", "fp_test_0001", CancellationToken.None);
        await _repository.AddToListAsync("trusted-players", "p-1001", CancellationToken.None);

        var mask = await _cache.CheckListsAsync(set, Card(), CancellationToken.None);

        Assert.Equal(0b110UL, mask);
    }

    public void Dispose() => _services.Dispose();
}
