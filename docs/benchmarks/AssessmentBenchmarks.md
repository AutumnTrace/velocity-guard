```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.7623/25H2/2025Update/HudsonValley2)
AMD Ryzen 7 9800X3D 4.70GHz, 1 CPU, 16 logical and 8 physical cores
.NET SDK 10.0.401
  [Host]     : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v4
  Job-TKQMQB : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v4

InvocationCount=100000  UnrollFactor=1  

```
| Method                                                     | Mean     | Error     | StdDev    | Median   | Gen0   | Gen1   | Allocated |
|----------------------------------------------------------- |---------:|----------:|----------:|---------:|-------:|-------:|----------:|
| &#39;Assess end to end in process (in-memory store, L1 cache)&#39; | 3.348 μs | 0.0930 μs | 0.2699 μs | 3.248 μs | 0.0600 | 0.0500 |   3.01 KB |
