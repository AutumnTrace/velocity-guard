using BenchmarkDotNet.Running;
using VelocityGuard.Benchmarks;

BenchmarkSwitcher.FromAssembly(typeof(RuleEvaluationBenchmarks).Assembly).Run(args);
