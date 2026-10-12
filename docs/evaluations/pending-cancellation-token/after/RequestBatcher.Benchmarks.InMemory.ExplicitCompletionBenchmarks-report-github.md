```

BenchmarkDotNet v0.15.8, macOS 27.0.1 (26A434) [Darwin 27.0.0]
Unknown processor
.NET SDK 10.0.100
  [Host]             : .NET 8.0.25 (8.0.25, 8.0.2526.11203), Arm64 RyuJIT armv8.0-a
  ExplicitCompletion : .NET 8.0.25 (8.0.25, 8.0.2526.11203), Arm64 RyuJIT armv8.0-a

Job=ExplicitCompletion  IterationCount=12  IterationTime=300ms  
LaunchCount=2  RunStrategy=Throughput  WarmupCount=5  

```
| Method                                         | GroupSize | Mean         | Error      | StdDev      | Rank | Gen0    | Gen1    | Gen2    | Allocated |
|----------------------------------------------- |---------- |-------------:|-----------:|------------:|-----:|--------:|--------:|--------:|----------:|
| &#39;Explicit group success&#39;                       | 10        |     9.444 μs |  0.1701 μs |   0.2152 μs |    1 |  0.6068 |  0.0607 |       - |   5.01 KB |
| &#39;Explicit groups with capacity waiting&#39;        | 10        |    23.289 μs |  0.3288 μs |   0.4038 μs |    2 |  0.9957 |  0.0766 |       - |   8.39 KB |
| &#39;Mixed single requests and one explicit group&#39; | 10        |    29.185 μs |  1.2242 μs |   1.5482 μs |    3 |  1.3238 |  0.0946 |       - |  11.21 KB |
| &#39;Explicit group partial caller cancellation&#39;   | 10        |    48.239 μs |  4.3164 μs |   5.6126 μs |    4 |  0.9527 |       - |       - |   8.25 KB |
| &#39;Explicit group success&#39;                       | 1000      |   178.296 μs |  3.8877 μs |   4.9167 μs |    5 | 21.0337 | 10.2163 |  2.4038 | 173.75 KB |
| &#39;Explicit group observed handler failure&#39;      | 10        |   237.643 μs |  2.7779 μs |   3.5132 μs |    6 |  0.8562 |       - |       - |  10.51 KB |
| &#39;Explicit groups with capacity waiting&#39;        | 1000      |   361.768 μs |  4.0384 μs |   5.1072 μs |    7 | 42.0673 | 20.4327 |  2.4038 | 350.29 KB |
| &#39;Explicit group partial caller cancellation&#39;   | 1000      |   626.802 μs | 86.6450 μs | 112.6630 μs |    8 | 58.8235 | 25.7353 |  3.6765 | 464.18 KB |
| &#39;Mixed single requests and one explicit group&#39; | 1000      | 1,039.094 μs | 18.4110 μs |  23.9396 μs |    9 | 56.2500 | 25.0000 | 12.5000 | 451.28 KB |
| &#39;Explicit group observed handler failure&#39;      | 1000      | 1,788.869 μs | 11.3912 μs |  14.8118 μs |   10 | 20.8333 |  6.9444 |       - | 207.18 KB |
