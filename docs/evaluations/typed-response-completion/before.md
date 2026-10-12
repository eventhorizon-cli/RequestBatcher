```

BenchmarkDotNet v0.15.8, macOS 27.0.1 (26A434) [Darwin 27.0.0]
Unknown processor
.NET SDK 10.0.100
  [Host]             : .NET 8.0.25 (8.0.25, 8.0.2526.11203), Arm64 RyuJIT armv8.0-a
  ResponseCompletion : .NET 8.0.25 (8.0.25, 8.0.2526.11203), Arm64 RyuJIT armv8.0-a

Job=ResponseCompletion  IterationCount=20  IterationTime=500ms
LaunchCount=2  RunStrategy=Throughput  WarmupCount=8

```
| Method                                 | Mean        | Error      | StdDev     | Rank | Gen0     | Gen1    | Gen2    | Allocated  |
|--------------------------------------- |------------:|-----------:|-----------:|-----:|---------:|--------:|--------:|-----------:|
| &#39;Response: explicit batch success&#39;     |    72.81 μs |   0.707 μs |   1.201 μs |    1 |   9.2540 |  4.5558 |  0.2847 |   76.38 KB |
| &#39;Request-only: single success control&#39; |   190.82 μs |   1.449 μs |   2.500 μs |    2 |  11.6460 |  5.4348 |  0.3882 |   93.91 KB |
| &#39;Response: single success&#39;             |   217.52 μs |   3.021 μs |   5.369 μs |    3 |  15.9396 |  7.5503 |  0.4195 |  131.12 KB |
| &#39;Response: pre-dispatch cancellation&#39;  | 8,096.03 μs | 110.295 μs | 190.253 μs |    4 |  62.5000 | 31.2500 | 15.6250 |  469.95 KB |
| &#39;Response: single failure&#39;             | 8,460.76 μs |  20.297 μs |  35.549 μs |    5 | 171.8750 | 46.8750 | 15.6250 | 1416.94 KB |
