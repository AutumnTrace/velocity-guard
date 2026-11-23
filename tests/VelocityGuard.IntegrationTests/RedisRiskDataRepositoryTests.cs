using VelocityGuard.Core.Velocity;
using VelocityGuard.Redis;

namespace VelocityGuard.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class RedisRiskDataRepositoryTests(RedisFixture redis)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly RedisRiskDataRepository _repository = new(redis.Connection, new RedisKeys(RedisFixture.UniquePrefix()));

    [Fact]
    public async Task Outcomes_build_up_the_player_profile()
    {
        var approvedAt = new DateTimeOffset(2026, 11, 23, 10, 30, 0, TimeSpan.Zero);

        var fresh = await _repository.GetProfileAsync("p-r1", Ct);
        await _repository.ApplyOutcomeAsync("p-r1", PaymentOutcome.Approved, approvedAt.AddDays(-1), Ct);
        await _repository.ApplyOutcomeAsync("p-r1", PaymentOutcome.Approved, approvedAt, Ct);
        await _repository.ApplyOutcomeAsync("p-r1", PaymentOutcome.Declined, approvedAt.AddHours(1), Ct);
        var afterChargeback = await _repository.ApplyOutcomeAsync("p-r1", PaymentOutcome.Chargeback, approvedAt.AddDays(30), Ct);

        Assert.Equal(0, fresh.ApprovedDeposits);
        Assert.Equal((2, 1, approvedAt), (afterChargeback.ApprovedDeposits, afterChargeback.Chargebacks, afterChargeback.LastApprovedAt!.Value));
        Assert.Equal(afterChargeback, await _repository.GetProfileAsync("p-r1", Ct));
    }

    [Fact]
    public async Task List_entries_can_be_added_checked_and_removed()
    {
        Assert.True(await _repository.AddToListAsync("blocked-devices", "dev-r1", Ct));
        Assert.False(await _repository.AddToListAsync("blocked-devices", "dev-r1", Ct));
        Assert.True(await _repository.IsListedAsync("blocked-devices", "dev-r1", Ct));
        Assert.True(await _repository.RemoveFromListAsync("blocked-devices", "dev-r1", Ct));
        Assert.False(await _repository.IsListedAsync("blocked-devices", "dev-r1", Ct));
    }

    [Fact]
    public async Task Replacing_a_list_swaps_every_entry_at_once()
    {
        await _repository.AddToListAsync("blocked-ips", "192.0.2.1", Ct);
        var many = Enumerable.Range(0, 2500).Select(i => $"198.51.{i / 250}.{i % 250}").ToArray();

        await _repository.ReplaceListAsync("blocked-ips", many, Ct);

        Assert.False(await _repository.IsListedAsync("blocked-ips", "192.0.2.1", Ct));
        Assert.True(await _repository.IsListedAsync("blocked-ips", "198.51.9.249", Ct));
        var page = await _repository.GetListAsync("blocked-ips", 100, Ct);
        Assert.Equal(100, page.Count);
        Assert.Equal(page.Order(StringComparer.Ordinal), page);

        await _repository.ReplaceListAsync("blocked-ips", [], Ct);
        Assert.Empty(await _repository.GetListAsync("blocked-ips", 10, Ct));
    }
}
