```

BenchmarkDotNet v0.15.8, macOS 27.0.1 (26A434) [Darwin 27.0.0]
Unknown processor
.NET SDK 10.0.100
  [Host]             : .NET 8.0.25 (8.0.25, 8.0.2526.11203), Arm64 RyuJIT armv8.0-a
  ExplicitSubmission : .NET 8.0.25 (8.0.25, 8.0.2526.11203), Arm64 RyuJIT armv8.0-a

Job=ExplicitSubmission  IterationCount=12  IterationTime=300ms
LaunchCount=2  RunStrategy=Throughput  WarmupCount=5

```
| Method                                 | GroupSize | Mean     | Error    | StdDev   | Rank | Gen0    | Gen1   | Gen2   | Allocated |
|--------------------------------------- |---------- |---------:|---------:|---------:|-----:|--------:|-------:|-------:|----------:|
| &#39;All requests in explicit groups&#39;      | 256       | 136.6 μs |  2.35 μs |  3.05 μs |    1 | 11.1301 | 3.8527 | 0.4281 |  92.76 KB |
| &#39;All requests in explicit groups&#39;      | 16        | 187.0 μs |  6.04 μs |  7.86 μs |    2 | 12.9717 | 4.7170 | 1.1792 | 110.43 KB |
| &#39;Half single and half explicit groups&#39; | 256       | 260.8 μs |  6.06 μs |  7.44 μs |    3 | 15.2439 | 6.0976 | 1.5244 | 124.12 KB |
| &#39;Half single and half explicit groups&#39; | 16        | 278.9 μs | 10.38 μs | 13.50 μs |    3 | 15.2027 | 5.0676 | 1.6892 | 129.45 KB |
