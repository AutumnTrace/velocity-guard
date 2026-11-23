```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.7623/25H2/2025Update/HudsonValley2)
AMD Ryzen 7 9800X3D 4.70GHz, 1 CPU, 16 logical and 8 physical cores
.NET SDK 10.0.401
  [Host]     : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v4
  Job-TKQMQB : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v4

InvocationCount=100000  UnrollFactor=1  

```
| Method                                         | Mean     | Error     | StdDev    | Gen0   | Gen1   | Allocated |
|----------------------------------------------- |---------:|----------:|----------:|-------:|-------:|----------:|
| &#39;Record attempt + read 7 windows (4 subjects)&#39; | 1.454 μs | 0.0291 μs | 0.0620 μs | 0.0300 | 0.0200 |   1.61 KB |
