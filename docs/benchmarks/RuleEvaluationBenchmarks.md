```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.7623/25H2/2025Update/HudsonValley2)
AMD Ryzen 7 9800X3D 4.70GHz, 1 CPU, 16 logical and 8 physical cores
.NET SDK 10.0.401
  [Host]     : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v4
  DefaultJob : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v4


```
| Method                                                | Mean     | Error    | StdDev   | Ratio | Allocated | Alloc Ratio |
|------------------------------------------------------ |---------:|---------:|---------:|------:|----------:|------------:|
| &#39;Evaluate 15 rules: clean attempt, no hits&#39;           | 30.89 ns | 0.063 ns | 0.056 ns |  1.00 |         - |          NA |
| &#39;Evaluate 15 rules: risky attempt, 8 hits + 1 shadow&#39; | 37.75 ns | 0.175 ns | 0.155 ns |  1.22 |         - |          NA |
