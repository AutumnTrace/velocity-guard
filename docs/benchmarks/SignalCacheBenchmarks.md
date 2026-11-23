```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.7623/25H2/2025Update/HudsonValley2)
AMD Ryzen 7 9800X3D 4.70GHz, 1 CPU, 16 logical and 8 physical cores
.NET SDK 10.0.401
  [Host]     : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v4
  DefaultJob : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v4


```
| Method                                                 | Mean      | Error    | StdDev   | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|------------------------------------------------------- |----------:|---------:|---------:|------:|--------:|-------:|----------:|------------:|
| &#39;Profile lookup, L1 hit&#39;                               |  45.58 ns | 0.665 ns | 0.555 ns |  1.00 |    0.02 | 0.0011 |      56 B |        1.00 |
| &#39;List membership lookup, L1 hit&#39;                       |  62.09 ns | 0.117 ns | 0.097 ns |  1.36 |    0.02 | 0.0017 |      88 B |        1.57 |
| &#39;Profile lookup, miss (evict, load from store, cache)&#39; | 371.66 ns | 6.721 ns | 6.287 ns |  8.16 |    0.16 | 0.0143 |     720 B |       12.86 |
