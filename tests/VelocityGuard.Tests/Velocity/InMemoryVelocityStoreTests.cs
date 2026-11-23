using FsCheck;
using FsCheck.Fluent;
using VelocityGuard.Core.Payments;
using VelocityGuard.Core.Rules;
using VelocityGuard.Core.Velocity;

namespace VelocityGuard.Tests.Velocity;

public sealed class InMemoryVelocityStoreTests : VelocityStoreContract
{
    protected override Task<(IVelocityStore Store, IAttemptJournal Journal)> CreateAsync()
    {
        var store = new InMemoryVelocityStore();
        return Task.FromResult<(IVelocityStore, IAttemptJournal)>((store, store));
    }

    [Fact]
    public async Task Sweep_forgets_idle_subjects_and_expired_journal_entries()
    {
        var store = new InMemoryVelocityStore(journalRetention: TimeSpan.FromHours(2));
        await Record(store, "a1", T0, device: "dev-idle");
        Assert.Equal(2, store.SubjectCount); // device + player

        store.Sweep(T0.AddHours(25).ToUnixTimeMilliseconds()); // past the 24h player window

        Assert.Equal(0, store.SubjectCount);
        Assert.Null(await store.FindAsync("a1", CancellationToken.None));
    }

    [Fact]
    public async Task Concurrent_recording_for_one_subject_loses_nothing()
    {
        var store = new InMemoryVelocityStore();

        var counts = await Task.WhenAll(Enumerable.Range(0, 200).Select(i =>
            Task.Run(() => Record(store, $"c{i}", T0.AddMilliseconds(i)))));

        // Every caller saw a distinct count: the read and the write are one atomic step per subject.
        Assert.Equal(Enumerable.Range(1, 200).Select(i => (long)i), counts.Select(c => c[3]).Order());
    }

    /// <summary>
    /// Property: for any sequence of attempts, the store agrees with a brute-force model that keeps
    /// every event and recounts the windows from scratch.
    /// </summary>
    [Fact]
    public void Agrees_with_a_brute_force_model_for_random_event_sequences()
    {
        var plan = PlanFor(
            new WindowSpec(Dimension.Device, TimeSpan.FromSeconds(60), WindowAggregate.Count),
            new WindowSpec(Dimension.Device, TimeSpan.FromSeconds(300), WindowAggregate.Sum));

        var step = Gen.Choose(0, 90_000).SelectMany(
            _ => Gen.Choose(0, 2),
            (gap, device) => (gap, device)).SelectMany(
            _ => Gen.Elements("EUR", "GBP"),
            (x, currency) => (x.gap, x.device, currency)).SelectMany(
            _ => Gen.Choose(1, 50_000),
            (x, minor) => (x.gap, x.device, x.currency, minor));

        Prop.ForAll(step.ArrayOf().ToArbitrary(), events =>
        {
            var store = new InMemoryVelocityStore();
            var history = new List<(long At, int Device, string Currency, long Minor)>();
            var now = T0;

            for (var i = 0; i < events.Length; i++)
            {
                var (gap, device, currency, minor) = events[i];
                now = now.AddMilliseconds(gap);
                var attempt = TestData.Card(
                    amount: minor / 100m,
                    currency: currency,
                    device: $"dev-{device}",
                    attemptId: $"p{i}");

                var values = store.RecordAsync(new VelocityRequest(attempt, plan, now), CancellationToken.None).AsTask().Result;

                var at = now.ToUnixTimeMilliseconds();
                history.Add((at, device, currency, minor));
                var expectedCount = history.Count(e => e.Device == device && e.At > at - 60_000);
                var expectedSum = history.Where(e => e.Device == device && e.Currency == currency && e.At > at - 300_000).Sum(e => e.Minor);

                if (values[0] != expectedCount || values[1] != expectedSum)
                {
                    return false;
                }
            }

            return true;
        }).QuickCheckThrowOnFailure();
    }
}
