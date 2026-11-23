using VelocityGuard.Core.Payments;
using VelocityGuard.Core.Rules;
using VelocityGuard.Core.Velocity;

namespace VelocityGuard.Tests.Velocity;

/// <summary>
/// Behaviour every <see cref="IVelocityStore"/> must have. The in-memory store runs it as a unit test;
/// the Redis store runs the same file against a real Redis in the integration tests.
/// </summary>
public abstract class VelocityStoreContract
{
    protected static readonly DateTimeOffset T0 = new(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);

    // Slot 0: device count 1m | slot 1: device count 10m | slot 2: player sum 24h | slot 3: player count 24h
    protected static readonly VelocityPlan Plan = PlanFor(
        new WindowSpec(Dimension.Device, TimeSpan.FromMinutes(1), WindowAggregate.Count),
        new WindowSpec(Dimension.Device, TimeSpan.FromMinutes(10), WindowAggregate.Count),
        new WindowSpec(Dimension.Player, TimeSpan.FromDays(1), WindowAggregate.Sum),
        new WindowSpec(Dimension.Player, TimeSpan.FromDays(1), WindowAggregate.Count));

    /// <summary>A fresh store, isolated from other tests (own Redis key prefix, or a new instance).</summary>
    protected abstract Task<(IVelocityStore Store, IAttemptJournal Journal)> CreateAsync();

    [Fact]
    public async Task Counts_every_attempt_inside_the_window_including_the_current_one()
    {
        var (store, _) = await CreateAsync();

        var first = await Record(store, "a1", T0);
        var second = await Record(store, "a2", T0.AddSeconds(10));
        var third = await Record(store, "a3", T0.AddSeconds(20));

        Assert.Equal([1, 2, 3], new[] { first[0], second[0], third[0] });
    }

    [Fact]
    public async Task An_event_exactly_one_window_old_is_outside_the_window()
    {
        var (store, _) = await CreateAsync();
        await Record(store, "a1", T0);

        var atBoundary = await Record(store, "a2", T0.AddMinutes(1));

        Assert.Equal(1, atBoundary[0]); // 1m window is (now - 1m, now]: a1 is out
        Assert.Equal(2, atBoundary[1]); // 10m window still has both
    }

    [Fact]
    public async Task An_event_one_millisecond_inside_the_window_is_counted()
    {
        var (store, _) = await CreateAsync();
        await Record(store, "a1", T0);

        var justInside = await Record(store, "a2", T0.AddMinutes(1).AddMilliseconds(-1));

        Assert.Equal(2, justInside[0]);
    }

    [Fact]
    public async Task Sums_only_amounts_in_the_attempt_currency()
    {
        var (store, _) = await CreateAsync();
        await Record(store, "a1", T0, amount: 100m, currency: "EUR");
        await Record(store, "a2", T0.AddSeconds(1), amount: 70m, currency: "GBP");

        var eur = await Record(store, "a3", T0.AddSeconds(2), amount: 25.50m, currency: "EUR");
        var gbp = await Record(store, "a4", T0.AddSeconds(3), amount: 5m, currency: "GBP");

        Assert.Equal(12_550, eur[2]);
        Assert.Equal(7_500, gbp[2]);
        Assert.Equal(4, gbp[3]);
    }

    [Fact]
    public async Task Recording_the_same_attempt_twice_counts_it_once()
    {
        var (store, _) = await CreateAsync();
        await Record(store, "a1", T0, amount: 40m);

        var retried = await Record(store, "a1", T0.AddSeconds(2), amount: 40m);

        Assert.Equal(1, retried[0]);
        Assert.Equal(4_000, retried[2]);
    }

    [Fact]
    public async Task A_released_amount_leaves_the_sum_but_the_attempt_still_counts()
    {
        var (store, _) = await CreateAsync();
        var declined = Attempt("a1", amount: 900m);
        await store.RecordAsync(new VelocityRequest(declined, Plan, T0), CancellationToken.None);

        await store.ReleaseAmountAsync(AttemptRecord.From(declined, T0), CancellationToken.None);
        await store.ReleaseAmountAsync(AttemptRecord.From(declined, T0), CancellationToken.None); // idempotent
        var next = await Record(store, "a2", T0.AddSeconds(5), amount: 10m);

        Assert.Equal(1_000, next[2]);
        Assert.Equal(2, next[3]);
    }

    [Fact]
    public async Task Subjects_are_counted_independently()
    {
        var (store, _) = await CreateAsync();
        await Record(store, "a1", T0, device: "dev-a");
        await Record(store, "a2", T0.AddSeconds(1), device: "dev-a");

        var other = await Record(store, "a3", T0.AddSeconds(2), device: "dev-b");

        Assert.Equal(1, other[0]);
        Assert.Equal(3, other[3]); // same player throughout
    }

    [Fact]
    public async Task A_missing_subject_is_skipped_and_reads_zero()
    {
        var (store, _) = await CreateAsync();

        var values = await Record(store, "a1", T0, device: null);

        Assert.Equal(0, values[0]);
        Assert.Equal(1, values[3]);
    }

    [Fact]
    public async Task Events_older_than_the_longest_window_are_trimmed()
    {
        var (store, _) = await CreateAsync();
        await Record(store, "a1", T0, amount: 500m);
        await Record(store, "a2", T0.AddHours(1), amount: 500m);

        var dayLater = await Record(store, "a3", T0.AddDays(1).AddHours(2), amount: 1m);

        Assert.Equal(100, dayLater[2]); // a1 and a2 both older than 24h
        Assert.Equal(1, dayLater[3]);
    }

    [Fact]
    public async Task Out_of_order_timestamps_land_in_the_right_place()
    {
        var (store, _) = await CreateAsync();
        await Record(store, "a1", T0.AddSeconds(50));

        // Another instance's clock is a little behind.
        var late = await Record(store, "a2", T0.AddSeconds(40));

        Assert.Equal(2, late[0]);
    }

    [Fact]
    public async Task The_journal_finds_assessed_attempts_and_enforces_outcome_transitions()
    {
        var (store, journal) = await CreateAsync();
        await Record(store, "a1", T0, amount: 12.34m);

        var record = await journal.FindAsync("a1", CancellationToken.None);
        Assert.NotNull(record);
        Assert.Equal(("EUR", 1_234L, "p-contract"), (record.Currency, record.AmountMinor, record.PlayerId));
        Assert.Null(await journal.FindAsync("nope", CancellationToken.None));

        Assert.Equal(OutcomeUpdateStatus.Applied, (await journal.SetOutcomeAsync("a1", PaymentOutcome.Approved, CancellationToken.None)).Status);
        Assert.Equal(OutcomeUpdateStatus.AlreadyRecorded, (await journal.SetOutcomeAsync("a1", PaymentOutcome.Approved, CancellationToken.None)).Status);
        Assert.Equal(OutcomeUpdateStatus.InvalidTransition, (await journal.SetOutcomeAsync("a1", PaymentOutcome.Declined, CancellationToken.None)).Status);

        var chargeback = await journal.SetOutcomeAsync("a1", PaymentOutcome.Chargeback, CancellationToken.None);
        Assert.Equal(OutcomeUpdateStatus.Applied, chargeback.Status);
        Assert.Equal(PaymentOutcome.Approved, chargeback.Previous);
        Assert.Equal(PaymentOutcome.Chargeback, (await journal.FindAsync("a1", CancellationToken.None))!.Outcome);

        Assert.Equal(OutcomeUpdateStatus.NotFound, (await journal.SetOutcomeAsync("nope", PaymentOutcome.Approved, CancellationToken.None)).Status);
    }

    protected static VelocityPlan PlanFor(params WindowSpec[] specs)
    {
        var builder = new VelocityPlan.Builder();
        foreach (var spec in specs)
        {
            builder.SlotFor(spec);
        }

        return builder.Build();
    }

    protected static PaymentAttempt Attempt(string id, decimal amount = 10m, string currency = "EUR", string? device = "dev-contract") =>
        TestData.Card(amount, currency, player: "p-contract", device: device, attemptId: id);

    protected static async Task<long[]> Record(
        IVelocityStore store,
        string id,
        DateTimeOffset at,
        decimal amount = 10m,
        string currency = "EUR",
        string? device = "dev-contract") =>
        await store.RecordAsync(new VelocityRequest(Attempt(id, amount, currency, device), Plan, at), CancellationToken.None);
}
