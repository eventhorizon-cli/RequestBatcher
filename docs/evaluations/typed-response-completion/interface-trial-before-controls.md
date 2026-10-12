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
| &#39;Response: explicit batch success&#39;     |  73.70 μs | 0.791 μs | 1.299 μs |    1 |  9.4918 | 4.8189 | 0.2921 |  76.38 KB |
| &#39;Request-only: single success control&#39; | 204.42 μs | 4.357 μs | 7.745 μs |    2 | 11.5031 | 5.3681 | 0.7669 |  93.99 KB |
