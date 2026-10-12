```

BenchmarkDotNet v0.15.8, macOS 27.0.1 (26A434) [Darwin 27.0.0]
Unknown processor
.NET SDK 10.0.100
  [Host]             : .NET 8.0.25 (8.0.25, 8.0.2526.11203), Arm64 RyuJIT armv8.0-a
  ExplicitCompletion : .NET 8.0.25 (8.0.25, 8.0.2526.11203), Arm64 RyuJIT armv8.0-a

Job=ExplicitCompletion  IterationCount=12  IterationTime=300ms  
LaunchCount=2  RunStrategy=Throughput  WarmupCount=5  

```
| Method                                         | GroupSize | Mean         | Error      | StdDev     | Rank | Gen0    | Gen1    | Gen2    | Allocated |
|----------------------------------------------- |---------- |-------------:|-----------:|-----------:|-----:|--------:|--------:|--------:|----------:|
| &#39;Explicit group success&#39;                       | 10        |     8.635 μs |  0.2653 μs |  0.3450 μs |    1 |  0.6253 |  0.0284 |       - |   5.11 KB |
| &#39;Explicit groups with capacity waiting&#39;        | 10        |    22.528 μs |  0.2508 μs |  0.3261 μs |    2 |  1.0204 |  0.1276 |       - |    8.6 KB |
| &#39;Mixed single requests and one explicit group&#39; | 10        |    27.055 μs |  0.7628 μs |  0.9918 μs |    3 |  1.2991 |  0.1443 |       - |  11.19 KB |
| &#39;Explicit group partial caller cancellation&#39;   | 10        |    43.061 μs |  0.3729 μs |  0.4579 μs |    4 |  0.9084 |       - |       - |   8.37 KB |
| &#39;Explicit group success&#39;                       | 1000      |   162.655 μs |  1.3416 μs |  1.6476 μs |    5 | 22.2458 | 10.5932 |  1.5890 | 181.58 KB |
| &#39;Explicit group observed handler failure&#39;      | 10        |   231.726 μs |  0.3661 μs |  0.4358 μs |    6 |  0.8117 |       - |       - |   10.6 KB |
| &#39;Explicit groups with capacity waiting&#39;        | 1000      |   331.636 μs |  6.5144 μs |  8.2386 μs |    7 | 44.9561 | 21.9298 |  2.1930 | 365.95 KB |
| &#39;Explicit group partial caller cancellation&#39;   | 1000      |   562.499 μs |  3.5973 μs |  4.5495 μs |    8 | 59.7826 | 27.1739 |  5.4348 | 472.01 KB |
| &#39;Mixed single requests and one explicit group&#39; | 1000      |   995.617 μs | 32.4375 μs | 42.1780 μs |    9 | 62.5000 | 31.2500 | 10.4167 | 467.07 KB |
| &#39;Explicit group observed handler failure&#39;      | 1000      | 1,708.030 μs |  9.9263 μs | 12.1904 μs |   10 | 20.8333 |  6.9444 |       - | 215.02 KB |
