```

BenchmarkDotNet v0.15.8, macOS 27.0.1 (26A434) [Darwin 27.0.0]
Unknown processor
.NET SDK 10.0.100
  [Host]             : .NET 8.0.25 (8.0.25, 8.0.2526.11203), Arm64 RyuJIT armv8.0-a
  ExplicitCompletion : .NET 8.0.25 (8.0.25, 8.0.2526.11203), Arm64 RyuJIT armv8.0-a

Job=ExplicitCompletion  IterationCount=12  IterationTime=300ms  
LaunchCount=2  RunStrategy=Throughput  WarmupCount=5  

```
| Method                                         | GroupSize | Mean         | Error      | StdDev     | Median       | Rank | Gen0    | Gen1    | Gen2    | Allocated |
|----------------------------------------------- |---------- |-------------:|-----------:|-----------:|-------------:|-----:|--------:|--------:|--------:|----------:|
| &#39;Explicit group success&#39;                       | 10        |     8.501 μs |  0.2534 μs |  0.3205 μs |     8.443 μs |    1 |  0.6120 |  0.0266 |       - |   5.09 KB |
| &#39;Explicit groups with capacity waiting&#39;        | 10        |    22.501 μs |  0.2303 μs |  0.2828 μs |    22.453 μs |    2 |  1.0417 |  0.1302 |       - |   8.56 KB |
| &#39;Mixed single requests and one explicit group&#39; | 10        |    24.931 μs |  1.6800 μs |  2.1845 μs |    24.739 μs |    2 |  1.3646 |  0.1365 |       - |  11.34 KB |
| &#39;Explicit group partial caller cancellation&#39;   | 10        |    43.168 μs |  0.4145 μs |  0.4934 μs |    43.359 μs |    3 |  0.9058 |       - |       - |   8.34 KB |
| &#39;Explicit group success&#39;                       | 1000      |   158.156 μs |  2.9857 μs |  3.6667 μs |   158.242 μs |    4 | 22.0588 | 10.5042 |  1.0504 | 181.56 KB |
| &#39;Explicit group observed handler failure&#39;      | 10        |   232.187 μs |  0.8014 μs |  1.0135 μs |   232.305 μs |    5 |  0.8224 |       - |       - |  10.58 KB |
| &#39;Explicit groups with capacity waiting&#39;        | 1000      |   341.444 μs | 13.4870 μs | 16.0553 μs |   328.178 μs |    6 | 43.7500 | 18.7500 |  6.2500 | 365.92 KB |
| &#39;Explicit group partial caller cancellation&#39;   | 1000      |   580.120 μs | 10.7566 μs | 12.3873 μs |   579.305 μs |    7 | 57.2917 | 26.0417 |  7.8125 | 471.98 KB |
| &#39;Mixed single requests and one explicit group&#39; | 1000      | 1,022.357 μs | 27.8508 μs | 36.2139 μs | 1,014.514 μs |    8 | 62.5000 | 28.4091 | 11.3636 | 467.34 KB |
| &#39;Explicit group observed handler failure&#39;      | 1000      | 1,707.274 μs | 21.2142 μs | 26.8291 μs | 1,713.035 μs |    9 | 20.8333 |  6.9444 |       - | 214.99 KB |
