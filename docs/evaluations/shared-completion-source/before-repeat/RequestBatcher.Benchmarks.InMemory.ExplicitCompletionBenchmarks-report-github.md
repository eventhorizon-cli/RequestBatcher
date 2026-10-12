```

BenchmarkDotNet v0.15.8, macOS 27.0.1 (26A434) [Darwin 27.0.0]
Unknown processor
.NET SDK 10.0.100
  [Host]             : .NET 8.0.25 (8.0.25, 8.0.2526.11203), Arm64 RyuJIT armv8.0-a
  ExplicitCompletion : .NET 8.0.25 (8.0.25, 8.0.2526.11203), Arm64 RyuJIT armv8.0-a

Job=ExplicitCompletion  IterationCount=12  IterationTime=300ms  
LaunchCount=2  RunStrategy=Throughput  WarmupCount=5  

```
| Method                                       | GroupSize | Mean      | Error     | StdDev    | Rank | Gen0    | Gen1    | Gen2   | Allocated |
|--------------------------------------------- |---------- |----------:|----------:|----------:|-----:|--------:|--------:|-------:|----------:|
| &#39;Explicit groups with capacity waiting&#39;      | 10        |  22.12 μs |  0.407 μs |  0.515 μs |    1 |  1.0046 |  0.0718 |      - |   8.59 KB |
| &#39;Explicit group partial caller cancellation&#39; | 10        |  43.00 μs |  0.421 μs |  0.533 μs |    2 |  0.8980 |       - |      - |   8.37 KB |
| &#39;Explicit groups with capacity waiting&#39;      | 1000      | 337.09 μs | 14.291 μs | 17.551 μs |    3 | 44.2708 | 20.8333 | 5.2083 | 365.95 KB |
| &#39;Explicit group partial caller cancellation&#39; | 1000      | 557.53 μs |  5.196 μs |  6.757 μs |    4 | 57.1429 | 26.7857 | 3.5714 |    472 KB |
