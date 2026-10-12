```

BenchmarkDotNet v0.15.8, macOS 27.0.1 (26A434) [Darwin 27.0.0]
Unknown processor
.NET SDK 10.0.100
  [Host]             : .NET 8.0.25 (8.0.25, 8.0.2526.11203), Arm64 RyuJIT armv8.0-a
  ResponseCompletion : .NET 8.0.25 (8.0.25, 8.0.2526.11203), Arm64 RyuJIT armv8.0-a

Job=ResponseCompletion  IterationCount=20  IterationTime=500ms
LaunchCount=2  RunStrategy=Throughput  WarmupCount=8

```
| Method                                 | Mean        | Error     | StdDev    | Rank | Gen0    | Gen1    | Gen2   | Allocated |
|--------------------------------------- |------------:|----------:|----------:|-----:|--------:|--------:|-------:|----------:|
| &#39;Response: explicit batch success&#39;     |    73.44 μs |  0.791 μs |  1.385 μs |    1 |  9.3115 |  4.5147 | 1.1287 |  76.38 KB |
| &#39;Request-only: single success control&#39; |   199.73 μs |  2.577 μs |  4.581 μs |    2 | 11.5741 |  5.4012 | 0.7716 |  93.85 KB |
| &#39;Response: single success&#39;             |   211.09 μs |  2.371 μs |  4.214 μs |    3 | 13.6589 |  6.6225 | 1.6556 | 111.29 KB |
| &#39;Response: pre-dispatch cancellation&#39;  | 6,358.94 μs | 19.517 μs | 33.666 μs |    4 | 25.0000 | 12.5000 |      - | 253.09 KB |
| &#39;Response: single failure&#39;             | 7,432.72 μs | 24.978 μs | 43.086 μs |    5 | 62.5000 | 15.6250 |      - | 574.16 KB |
