```

BenchmarkDotNet v0.15.8, macOS 27.0.1 (26A434) [Darwin 27.0.0]
Unknown processor
.NET SDK 10.0.100
  [Host]                 : .NET 8.0.25 (8.0.25, 8.0.2526.11203), Arm64 RyuJIT armv8.0-a
  PartitionKeySubmission : .NET 8.0.25 (8.0.25, 8.0.2526.11203), Arm64 RyuJIT armv8.0-a

Job=PartitionKeySubmission  IterationCount=12  IterationTime=300ms
LaunchCount=2  RunStrategy=Throughput  WarmupCount=5

```
| Method                                  | RequestCount | DelayMilliseconds | Mean      | Error     | StdDev    | Ratio | RatioSD | Rank | Gen0    | Gen1    | Allocated | Alloc Ratio |
|---------------------------------------- |------------- |------------------ |----------:|----------:|----------:|------:|--------:|-----:|--------:|--------:|----------:|------------:|
| &#39;One explicit submission&#39;               | 10           | 1                 |  1.181 ms | 0.0091 ms | 0.0115 ms |  0.96 |    0.02 |    1 |       - |       - |   9.08 KB |        0.84 |
| &#39;Concurrent single-request submissions&#39; | 10           | 1                 |  1.228 ms | 0.0239 ms | 0.0294 ms |  1.00 |    0.03 |    1 |       - |       - |  10.76 KB |        1.00 |
|                                         |              |                   |           |           |           |       |         |      |         |         |           |             |
| &#39;Concurrent single-request submissions&#39; | 10           | 10                | 11.023 ms | 0.0367 ms | 0.0451 ms |  1.00 |    0.01 |    1 |       - |       - |  10.17 KB |        1.00 |
| &#39;One explicit submission&#39;               | 10           | 10                | 11.069 ms | 0.0493 ms | 0.0587 ms |  1.00 |    0.01 |    1 |       - |       - |   9.08 KB |        0.89 |
|                                         |              |                   |           |           |           |       |         |      |         |         |           |             |
| &#39;One explicit submission&#39;               | 100          | 1                 |  1.257 ms | 0.0245 ms | 0.0300 ms |  0.55 |    0.02 |    1 |       - |       - |  23.22 KB |        0.52 |
| &#39;Concurrent single-request submissions&#39; | 100          | 1                 |  2.291 ms | 0.0306 ms | 0.0387 ms |  1.00 |    0.02 |    2 |       - |       - |  44.59 KB |        1.00 |
|                                         |              |                   |           |           |           |       |         |      |         |         |           |             |
| &#39;One explicit submission&#39;               | 100          | 10                | 11.137 ms | 0.0738 ms | 0.0879 ms |  0.66 |    0.08 |    1 |       - |       - |  23.22 KB |        0.58 |
| &#39;Concurrent single-request submissions&#39; | 100          | 10                | 17.050 ms | 1.4557 ms | 1.8928 ms |  1.01 |    0.16 |    2 |       - |       - |  40.16 KB |        1.00 |
|                                         |              |                   |           |           |           |       |         |      |         |         |           |             |
| &#39;One explicit submission&#39;               | 1000         | 1                 |  2.707 ms | 0.0654 ms | 0.0803 ms |  0.73 |    0.03 |    1 | 15.6250 |  7.8125 | 175.57 KB |        0.61 |
| &#39;Concurrent single-request submissions&#39; | 1000         | 1                 |  3.707 ms | 0.0653 ms | 0.0849 ms |  1.00 |    0.03 |    2 | 25.0000 | 12.5000 | 285.69 KB |        1.00 |
|                                         |              |                   |           |           |           |       |         |      |         |         |           |             |
| &#39;One explicit submission&#39;               | 1000         | 10                | 22.457 ms | 0.1781 ms | 0.2120 ms |  0.71 |    0.03 |    1 |       - |       - | 175.09 KB |        0.62 |
| &#39;Concurrent single-request submissions&#39; | 1000         | 10                | 31.610 ms | 1.0671 ms | 1.3875 ms |  1.00 |    0.06 |    2 |       - |       - | 282.59 KB |        1.00 |
