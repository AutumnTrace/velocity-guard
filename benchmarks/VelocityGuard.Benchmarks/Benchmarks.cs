using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using VelocityGuard.Core.Assessment;
using VelocityGuard.Core.Evaluation;
using VelocityGuard.Core.Payments;
using VelocityGuard.Core.Rules;
using VelocityGuard.Core.Signals;
using VelocityGuard.Core.Velocity;

namespace VelocityGuard.Benchmarks;

/// <summary>
/// The rules engine alone: the 15 rules of the shipped rules.json against signals that are already gathered.
/// This is the part that runs for every assessment and must not allocate.
/// </summary>
[MemoryDiagnoser]
public class RuleEvaluationBenchmarks
{
    private CompiledRuleSet _rules = null!;
    private RuleResult[] _results = null!;
    private PaymentAttempt _clean = null!;
    private PaymentAttempt _risky = null!;
    private long[] _quietWindows = null!;
    private long[] _busyWindows = null!;
    private ulong _riskyListHits;

    [GlobalSetup]
    public void Setup()
    {
        _rules = Bench.ShippedRules();
        _results = new RuleResult[_rules.Count];
        _clean = Bench.Attempt(1);
        _risky = Bench.Attempt(2, amount: 600m) with { AccountAgeDays = 2, CardBinCountry = "BR" };

        _quietWindows = new long[_rules.Velocity.SlotCount];
        Array.Fill(_quietWindows, 1);
        _busyWindows = new long[_rules.Velocity.SlotCount];
        Array.Fill(_busyWindows, 1_000_000); // every count over its max, every sum over its limit

        var blockedDevice = _rules.ListChecks.IndexOf(new ListCheck("blocked-devices", Dimension.Device));
        _riskyListHits = 1UL << blockedDevice;
    }

    [Benchmark(Baseline = true, Description = "Evaluate 15 rules: clean attempt, no hits")]
    public EvaluationSummary Clean()
    {
        var context = new RiskContext(_clean, _quietWindows, 0, PlayerProfile.New, SignalAvailability.All);
        return _rules.Evaluate(in context, _results);
    }

    [Benchmark(Description = "Evaluate 15 rules: risky attempt, 8 hits + 1 shadow")]
    public EvaluationSummary Risky()
    {
        var context = new RiskContext(_risky, _busyWindows, _riskyListHits, PlayerProfile.New, SignalAvailability.All);
        return _rules.Evaluate(in context, _results);
    }
}

/// <summary>HybridCache in front of the system of record: the L1 hit path every assessment takes, and a full miss.</summary>
[MemoryDiagnoser]
public class SignalCacheBenchmarks
{
    private ServiceProvider _services = null!;
    private RiskSignalCache _cache = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        _services = Bench.Services(TimeProvider.System);
        _cache = _services.GetRequiredService<RiskSignalCache>();
        await _cache.GetProfileAsync("p-hot", CancellationToken.None);
        await _cache.IsListedAsync("blocked-devices", "dev-hot", CancellationToken.None);
    }

    [GlobalCleanup]
    public void Cleanup() => _services.Dispose();

    [Benchmark(Baseline = true, Description = "Profile lookup, L1 hit")]
    public ValueTask<PlayerProfile> ProfileHit() => _cache.GetProfileAsync("p-hot", CancellationToken.None);

    [Benchmark(Description = "List membership lookup, L1 hit")]
    public ValueTask<bool> ListHit() => _cache.IsListedAsync("blocked-devices", "dev-hot", CancellationToken.None);

    [Benchmark(Description = "Profile lookup, miss (evict, load from store, cache)")]
    public async ValueTask<PlayerProfile> ProfileMiss()
    {
        await _cache.ProfileChangedAsync("p-cold", CancellationToken.None);
        return await _cache.GetProfileAsync("p-cold", CancellationToken.None);
    }
}

/// <summary>
/// The in-memory sliding-window store recording one attempt for 4 subjects across the 7 windows of the
/// shipped rules. A fresh store per iteration; 100 ms of simulated time between attempts.
/// </summary>
[MemoryDiagnoser]
public class InMemoryVelocityStoreBenchmarks
{
    private const int OperationsPerIteration = 100_000;

    private VelocityPlan _plan = null!;
    private PaymentAttempt[] _attempts = null!;
    private InMemoryVelocityStore _store = null!;
    private int _next;

    [GlobalSetup]
    public void Setup()
    {
        _plan = Bench.ShippedRules().Velocity;
        _attempts = [.. Enumerable.Range(0, OperationsPerIteration).Select(i => Bench.Attempt(i))];
    }

    [IterationSetup]
    public void FreshStore()
    {
        _store = new InMemoryVelocityStore();
        _next = 0;
    }

    [Benchmark(Description = "Record attempt + read 7 windows (4 subjects)")]
    [InvocationCount(OperationsPerIteration)]
    public ValueTask<long[]> Record()
    {
        var i = _next++;
        return _store.RecordAsync(new VelocityRequest(_attempts[i], _plan, Bench.Start.AddMilliseconds(i * 100L)), CancellationToken.None);
    }
}

/// <summary>
/// A whole assessment in process, minus HTTP: HybridCache lookups (mostly L1 hits), the in-memory velocity store,
/// rule evaluation, decision and the response model. The Redis round trip of a real deployment is not included.
/// </summary>
[MemoryDiagnoser]
public class AssessmentBenchmarks
{
    private const int OperationsPerIteration = 100_000;

    private PaymentAttempt[] _attempts = null!;
    private ServiceProvider _services = null!;
    private AssessmentService _service = null!;
    private int _next;

    [GlobalSetup]
    public void Setup() => _attempts = [.. Enumerable.Range(0, OperationsPerIteration).Select(i => Bench.Attempt(i))];

    [IterationSetup]
    public void FreshServices()
    {
        _services?.Dispose();
        _services = Bench.Services(new SteppingTimeProvider(Bench.Start, TimeSpan.FromMilliseconds(100)));
        _service = _services.GetRequiredService<AssessmentService>();
        _next = 0;
    }

    [GlobalCleanup]
    public void Cleanup() => _services.Dispose();

    [Benchmark(Description = "Assess end to end in process (in-memory store, L1 cache)")]
    [InvocationCount(OperationsPerIteration)]
    public ValueTask<AssessmentResult> Assess() => _service.AssessAsync(_attempts[_next++], CancellationToken.None);
}
