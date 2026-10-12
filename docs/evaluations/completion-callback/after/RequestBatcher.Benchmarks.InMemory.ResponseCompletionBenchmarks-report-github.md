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
| &#39;Response: explicit batch success&#39;     |    66.07 μs |  0.513 μs |  0.911 μs |    1 |  7.3996 |  3.5677 | 0.1321 |  60.38 KB |
| &#39;Request-only: single success control&#39; |   200.82 μs |  3.107 μs |  5.522 μs |    2 |  9.4697 |  4.1667 | 0.7576 |  77.88 KB |
| &#39;Response: single success&#39;             |   202.36 μs |  2.643 μs |  4.698 μs |    2 | 11.5489 |  5.4348 | 0.6793 |  95.62 KB |
| &#39;Response: pre-dispatch cancellation&#39;  | 6,308.79 μs | 20.396 μs | 35.723 μs |    3 | 25.0000 |       - |      - | 236.96 KB |
| &#39;Response: single failure&#39;             | 7,420.46 μs | 37.414 μs | 65.527 μs |    4 | 62.5000 | 15.6250 |      - | 555.56 KB |
