# Benchmark results

Raw BenchmarkDotNet reports from the run quoted in the main README, copied unedited from
`BenchmarkDotNet.Artifacts/results/*-report-github.md`. Re-run them with:

```bash
cd benchmarks/VelocityGuard.Benchmarks
dotnet run -c Release -- --filter "*"
```

| Report | What it measures |
|---|---|
| [RuleEvaluationBenchmarks](RuleEvaluationBenchmarks.md) | The 15 shipped rules evaluated against gathered signals (the hot path, no I/O) |
| [SignalCacheBenchmarks](SignalCacheBenchmarks.md) | HybridCache L1 hits for a profile and a list entry, and a full miss |
| [InMemoryVelocityStoreBenchmarks](InMemoryVelocityStoreBenchmarks.md) | Recording one attempt in the in-memory store and reading 7 windows for 4 subjects |
| [AssessmentBenchmarks](AssessmentBenchmarks.md) | A whole assessment in process: cache, in-memory store, evaluation, result model (no HTTP, no Redis) |

These numbers come from one developer workstation. They are useful for comparing changes on the same machine, not as a promise for production hardware. The Redis path is not benchmarked here: its cost is dominated by the network round trip.
