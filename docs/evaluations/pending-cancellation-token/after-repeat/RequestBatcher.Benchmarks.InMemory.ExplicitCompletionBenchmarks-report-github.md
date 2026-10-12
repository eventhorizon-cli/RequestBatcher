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
| &#39;Explicit group success&#39;                       | 10        |     9.504 μs |  0.3240 μs |  0.4212 μs |     9.437 μs |    1 |  0.6050 |  0.0303 |       - |   5.01 KB |
| &#39;Explicit groups with capacity waiting&#39;        | 10        |    21.628 μs |  0.3875 μs |  0.4900 μs |    21.400 μs |    2 |  0.9679 |  0.1383 |       - |   8.38 KB |
| &#39;Mixed single requests and one explicit group&#39; | 10        |    29.823 μs |  1.5675 μs |  2.0382 μs |    29.660 μs |    3 |  1.3607 |  0.0907 |       - |  11.46 KB |
| &#39;Explicit group partial caller cancellation&#39;   | 10        |    45.653 μs |  0.5377 μs |  0.6603 μs |    45.652 μs |    4 |  0.9827 |       - |       - |   8.26 KB |
| &#39;Explicit group success&#39;                       | 1000      |   180.174 μs |  4.5838 μs |  5.9602 μs |   179.607 μs |    5 | 21.2156 |  9.7477 |  1.7202 | 173.74 KB |
| &#39;Explicit group observed handler failure&#39;      | 10        |   239.301 μs |  0.5014 μs |  0.6157 μs |   239.123 μs |    6 |  0.8562 |       - |       - |  10.51 KB |
| &#39;Explicit groups with capacity waiting&#39;        | 1000      |   330.163 μs |  5.0587 μs |  6.3977 μs |   328.705 μs |    7 | 42.0455 | 14.7727 |  1.1364 | 350.27 KB |
| &#39;Explicit group partial caller cancellation&#39;   | 1000      |   621.704 μs | 71.8957 μs | 93.4847 μs |   562.988 μs |    8 | 62.5000 | 34.7222 |  6.9444 | 464.21 KB |
| &#39;Mixed single requests and one explicit group&#39; | 1000      |   873.833 μs | 27.9547 μs | 35.3537 μs |   876.632 μs |    9 | 52.0833 | 26.0417 | 10.4167 | 451.33 KB |
| &#39;Explicit group observed handler failure&#39;      | 1000      | 1,804.696 μs |  7.4340 μs |  9.4017 μs | 1,803.372 μs |   10 | 20.8333 |  6.9444 |       - | 207.19 KB |
