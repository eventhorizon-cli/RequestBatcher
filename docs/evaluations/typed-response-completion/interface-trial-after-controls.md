```

BenchmarkDotNet v0.15.8, macOS 27.0.1 (26A434) [Darwin 27.0.0]
Unknown processor
.NET SDK 10.0.100
  [Host]             : .NET 8.0.25 (8.0.25, 8.0.2526.11203), Arm64 RyuJIT armv8.0-a
  ResponseCompletion : .NET 8.0.25 (8.0.25, 8.0.2526.11203), Arm64 RyuJIT armv8.0-a

Job=ResponseCompletion  IterationCount=20  IterationTime=500ms
LaunchCount=2  RunStrategy=Throughput  WarmupCount=8

```
| Method                                 | Mean      | Error    | StdDev   | Rank | Gen0    | Gen1   | Gen2   | Allocated |
|--------------------------------------- |----------:|---------:|---------:|-----:|--------:|-------:|-------:|----------:|
| &#39;Response: explicit batch success&#39;     |  78.20 μs | 1.268 μs | 2.188 μs |    1 |  9.2762 | 4.5620 | 2.2810 |  76.39 KB |
| &#39;Request-only: single success control&#39; | 199.45 μs | 2.999 μs | 5.330 μs |    2 | 11.2903 | 5.2419 | 0.8065 |  93.99 KB |
