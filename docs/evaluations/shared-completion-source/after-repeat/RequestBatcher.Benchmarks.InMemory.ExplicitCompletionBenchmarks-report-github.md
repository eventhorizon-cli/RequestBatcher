```

BenchmarkDotNet v0.15.8, macOS 27.0.1 (26A434) [Darwin 27.0.0]
Unknown processor
.NET SDK 10.0.100
  [Host]             : .NET 8.0.25 (8.0.25, 8.0.2526.11203), Arm64 RyuJIT armv8.0-a
  ExplicitCompletion : .NET 8.0.25 (8.0.25, 8.0.2526.11203), Arm64 RyuJIT armv8.0-a

Job=ExplicitCompletion  IterationCount=12  IterationTime=300ms  
LaunchCount=2  RunStrategy=Throughput  WarmupCount=5  

```
| Method                                       | GroupSize | Mean      | Error     | StdDev    | Rank | Gen0    | Gen1    | Gen2    | Allocated |
|--------------------------------------------- |---------- |----------:|----------:|----------:|-----:|--------:|--------:|--------:|----------:|
| &#39;Explicit groups with capacity waiting&#39;      | 10        |  21.94 μs |  0.538 μs |  0.681 μs |    1 |  1.0246 |  0.1281 |       - |   8.57 KB |
| &#39;Explicit group partial caller cancellation&#39; | 10        |  44.20 μs |  1.692 μs |  2.139 μs |    2 |  0.9032 |       - |       - |   8.34 KB |
| &#39;Explicit groups with capacity waiting&#39;      | 1000      | 325.95 μs |  3.483 μs |  4.528 μs |    3 | 45.8333 | 16.6667 |  2.0833 | 365.91 KB |
| &#39;Explicit group partial caller cancellation&#39; | 1000      | 582.94 μs | 36.261 μs | 41.758 μs |    4 | 55.5556 | 20.8333 | 13.8889 | 471.99 KB |
