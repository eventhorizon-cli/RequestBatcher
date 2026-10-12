```

BenchmarkDotNet v0.15.8, macOS 27.0.1 (26A434) [Darwin 27.0.0]
Unknown processor
.NET SDK 10.0.100
  [Host]             : .NET 8.0.25 (8.0.25, 8.0.2526.11203), Arm64 RyuJIT armv8.0-a
  ExplicitSubmission : .NET 8.0.25 (8.0.25, 8.0.2526.11203), Arm64 RyuJIT armv8.0-a

Job=ExplicitSubmission  IterationCount=12  IterationTime=300ms
LaunchCount=2  RunStrategy=Throughput  WarmupCount=5

```
| Method                                 | GroupSize | Mean     | Error   | StdDev  | Rank | Gen0    | Gen1   | Gen2   | Allocated |
|--------------------------------------- |---------- |---------:|--------:|--------:|-----:|--------:|-------:|-------:|----------:|
| &#39;All requests in explicit groups&#39;      | 256       | 143.4 μs | 3.22 μs | 4.18 μs |    1 | 15.3986 | 5.4348 | 0.9058 |  124.7 KB |
| &#39;All requests in explicit groups&#39;      | 16        | 193.2 μs | 2.70 μs | 3.32 μs |    2 | 17.0455 | 7.5758 | 1.2626 | 142.39 KB |
| &#39;Half single and half explicit groups&#39; | 256       | 285.7 μs | 5.55 μs | 7.21 μs |    3 | 18.5811 | 6.7568 | 3.3784 |  155.7 KB |
| &#39;Half single and half explicit groups&#39; | 16        | 291.7 μs | 6.99 μs | 9.08 μs |    3 | 19.7368 | 8.2237 | 3.2895 | 161.48 KB |
