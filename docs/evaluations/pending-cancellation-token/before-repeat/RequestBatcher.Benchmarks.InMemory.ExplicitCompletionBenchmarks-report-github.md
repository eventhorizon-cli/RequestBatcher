```

BenchmarkDotNet v0.15.8, macOS 27.0.1 (26A434) [Darwin 27.0.0]
Unknown processor
.NET SDK 10.0.100
  [Host]             : .NET 8.0.25 (8.0.25, 8.0.2526.11203), Arm64 RyuJIT armv8.0-a
  ExplicitCompletion : .NET 8.0.25 (8.0.25, 8.0.2526.11203), Arm64 RyuJIT armv8.0-a

Job=ExplicitCompletion  IterationCount=12  IterationTime=300ms  
LaunchCount=2  RunStrategy=Throughput  WarmupCount=5  

```
| Method                                         | GroupSize | Mean        | Error     | StdDev     | Median      | Rank | Gen0    | Gen1    | Gen2   | Allocated |
|----------------------------------------------- |---------- |------------:|----------:|-----------:|------------:|-----:|--------:|--------:|-------:|----------:|
| &#39;Explicit group success&#39;                       | 10        |    10.39 μs |  0.222 μs |   0.281 μs |    10.32 μs |    1 |  0.6247 |  0.0347 |      - |   5.09 KB |
| &#39;Explicit groups with capacity waiting&#39;        | 10        |    24.21 μs |  0.805 μs |   1.047 μs |    23.88 μs |    2 |  1.0364 |  0.0797 |      - |   8.55 KB |
| &#39;Mixed single requests and one explicit group&#39; | 10        |    30.59 μs |  0.900 μs |   1.170 μs |    30.39 μs |    3 |  1.2830 |  0.1833 |      - |  11.26 KB |
| &#39;Explicit group partial caller cancellation&#39;   | 10        |    46.04 μs |  0.386 μs |   0.501 μs |    46.00 μs |    4 |  0.9735 |       - |      - |   8.34 KB |
| &#39;Explicit group success&#39;                       | 1000      |   188.84 μs |  1.991 μs |   2.446 μs |   188.15 μs |    5 | 22.6716 | 11.6422 | 0.6127 | 181.56 KB |
| &#39;Explicit group observed handler failure&#39;      | 10        |   243.50 μs |  0.720 μs |   0.937 μs |   243.43 μs |    6 |  0.8803 |       - |      - |  10.58 KB |
| &#39;Explicit groups with capacity waiting&#39;        | 1000      |   365.85 μs |  4.002 μs |   5.062 μs |   365.74 μs |    7 | 44.6429 | 20.8333 | 2.9762 | 365.92 KB |
| &#39;Explicit group partial caller cancellation&#39;   | 1000      |   672.69 μs | 92.686 μs | 117.218 μs |   600.61 μs |    8 | 62.5000 | 31.2500 | 7.8125 | 471.99 KB |
| &#39;Mixed single requests and one explicit group&#39; | 1000      |   896.25 μs | 34.730 μs |  45.159 μs |   896.28 μs |    9 | 57.2917 | 26.0417 | 5.2083 | 466.58 KB |
| &#39;Explicit group observed handler failure&#39;      | 1000      | 1,803.84 μs | 19.681 μs |  24.170 μs | 1,808.16 μs |   10 | 20.8333 | 13.8889 |      - | 214.99 KB |
