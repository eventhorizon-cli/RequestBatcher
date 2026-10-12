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
| &#39;Response: explicit batch success&#39;     |    71.50 μs |  0.598 μs |  1.000 μs |    1 |  9.3680 |  4.6141 | 0.1398 |  76.38 KB |
| &#39;Request-only: single success control&#39; |   203.75 μs |  2.668 μs |  4.743 μs |    2 | 11.3281 |  5.4688 | 0.7813 |  94.08 KB |
| &#39;Response: single success&#39;             |   211.64 μs |  2.997 μs |  5.327 μs |    2 | 13.6589 |  6.6225 | 0.8278 | 111.31 KB |
| &#39;Response: pre-dispatch cancellation&#39;  | 6,365.37 μs |  9.949 μs | 15.781 μs |    3 | 25.0000 | 12.5000 |      - | 253.09 KB |
| &#39;Response: single failure&#39;             | 7,425.80 μs | 41.242 μs | 73.308 μs |    4 | 62.5000 | 15.6250 |      - | 571.95 KB |
