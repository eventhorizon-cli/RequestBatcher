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
| &#39;Explicit group success&#39;                       | 10        |     8.379 μs |  0.2624 μs |  0.3411 μs |    1 |  0.6048 |  0.0288 |       - |   5.09 KB |
| &#39;Explicit groups with capacity waiting&#39;        | 10        |    22.669 μs |  0.4467 μs |  0.5650 μs |    2 |  1.0442 |  0.0746 |       - |   8.55 KB |
| &#39;Mixed single requests and one explicit group&#39; | 10        |    24.306 μs |  1.4364 μs |  1.8166 μs |    2 |  1.2438 |  0.1555 |       - |  11.11 KB |
| &#39;Explicit group partial caller cancellation&#39;   | 10        |    43.144 μs |  0.3413 μs |  0.4063 μs |    3 |  0.9084 |       - |       - |   8.34 KB |
| &#39;Explicit group success&#39;                       | 1000      |   161.394 μs |  4.6419 μs |  5.5258 μs |    4 | 22.2458 | 10.5932 |  1.5890 | 181.56 KB |
| &#39;Explicit group observed handler failure&#39;      | 10        |   232.442 μs |  0.7542 μs |  0.9263 μs |    5 |  0.8224 |       - |       - |  10.59 KB |
| &#39;Explicit groups with capacity waiting&#39;        | 1000      |   348.933 μs |  9.8122 μs | 12.0503 μs |    6 | 45.7589 | 22.3214 |  2.2321 | 365.93 KB |
| &#39;Explicit group partial caller cancellation&#39;   | 1000      |   634.822 μs | 77.3697 μs | 95.0170 μs |    7 | 56.2500 | 25.0000 | 12.5000 | 471.98 KB |
| &#39;Mixed single requests and one explicit group&#39; | 1000      | 1,033.322 μs | 18.0926 μs | 23.5255 μs |    8 | 62.5000 | 28.4091 | 11.3636 | 466.98 KB |
| &#39;Explicit group observed handler failure&#39;      | 1000      | 1,729.805 μs | 10.9019 μs | 13.3885 μs |    9 | 20.8333 |  6.9444 |       - | 214.99 KB |
