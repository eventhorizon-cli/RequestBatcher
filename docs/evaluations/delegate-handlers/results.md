```

BenchmarkDotNet v0.15.8, macOS 27.0.1 (26A434) [Darwin 27.0.0]
Unknown processor
.NET SDK 10.0.100
  [Host]           : .NET 8.0.25 (8.0.25, 8.0.2526.11203), Arm64 RyuJIT armv8.0-a
  DelegateHandlers : .NET 8.0.25 (8.0.25, 8.0.2526.11203), Arm64 RyuJIT armv8.0-a

Job=DelegateHandlers  IterationCount=12  IterationTime=300ms
LaunchCount=2  RunStrategy=Throughput  WarmupCount=5

```
| Method                      | Categories          | Mean      | Error    | StdDev   | Median    | Ratio | RatioSD | Gen0    | Gen1   | Gen2   | Allocated | Alloc Ratio |
|---------------------------- |-------------------- |----------:|---------:|---------:|----------:|------:|--------:|--------:|-------:|-------:|----------:|------------:|
| TypedBatchNoServices        | BatchNoServices     |  71.78 μs | 6.721 μs | 8.499 μs |  65.75 μs |  1.01 |    0.17 |  6.7568 | 3.1672 | 0.2111 |  55.82 KB |        1.00 |
| DelegateBatchNoServices     | BatchNoServices     |  63.36 μs | 1.526 μs | 1.984 μs |  63.51 μs |  0.89 |    0.11 |  6.8069 | 3.0941 | 0.2063 |  55.82 KB |        1.00 |
|                             |                     |           |          |          |           |       |         |         |        |        |           |             |
| TypedBatchScopedService     | BatchScopedService  |  66.37 μs | 2.032 μs | 2.643 μs |  66.11 μs |  1.00 |    0.05 |  7.1367 | 3.2439 | 0.2163 |  59.51 KB |        1.00 |
| DelegateBatchScopedService  | BatchScopedService  |  63.43 μs | 1.025 μs | 1.333 μs |  63.50 μs |  0.96 |    0.04 |  7.1656 | 3.3838 | 0.1990 |  59.13 KB |        0.99 |
|                             |                     |           |          |          |           |       |         |         |        |        |           |             |
| TypedSingleNoServices       | SingleNoServices    | 209.75 μs | 3.409 μs | 4.311 μs | 209.02 μs |  1.00 |    0.03 | 10.9890 | 4.8077 | 0.6868 |  90.26 KB |        1.00 |
| DelegateSingleNoServices    | SingleNoServices    | 212.59 μs | 4.973 μs | 6.108 μs | 213.75 μs |  1.01 |    0.03 | 10.4167 | 3.4722 | 1.1574 |   90.6 KB |        1.00 |
|                             |                     |           |          |          |           |       |         |         |        |        |           |             |
| TypedSingleScopedService    | SingleScopedService | 212.09 μs | 2.913 μs | 3.788 μs | 212.23 μs |  1.00 |    0.02 | 11.9382 | 4.9157 | 0.7022 |  97.21 KB |        1.00 |
| DelegateSingleScopedService | SingleScopedService | 210.04 μs | 1.530 μs | 1.989 μs | 210.00 μs |  0.99 |    0.02 | 11.0294 | 3.6765 | 1.2255 |  96.46 KB |        0.99 |
