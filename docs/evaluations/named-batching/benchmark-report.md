```

BenchmarkDotNet v0.15.8, macOS 27.0.1 (26A434) [Darwin 27.0.0]
Unknown processor
.NET SDK 10.0.100
  [Host]        : .NET 8.0.25 (8.0.25, 8.0.2526.11203), Arm64 RyuJIT armv8.0-a
  NamedBatching : .NET 8.0.25 (8.0.25, 8.0.2526.11203), Arm64 RyuJIT armv8.0-a

Job=NamedBatching  IterationCount=12  IterationTime=300ms
LaunchCount=2  RunStrategy=Throughput  WarmupCount=5

```
| Method               | Categories | HandlerLifetime | Mean      | Error    | StdDev   | Ratio | RatioSD | Gen0    | Gen1   | Gen2   | Allocated | Alloc Ratio |
|--------------------- |----------- |---------------- |----------:|---------:|---------:|------:|--------:|--------:|-------:|-------:|----------:|------------:|
| **UnnamedExplicitAsync** | **Explicit**   | **Singleton**       |  **65.17 μs** | **1.364 μs** | **1.675 μs** |  **1.00** |    **0.04** |  **6.7797** | **3.1780** | **0.2119** |  **55.82 KB** |        **1.00** |
| NamedExplicitAsync   | Explicit   | Singleton       |  66.27 μs | 1.821 μs | 2.368 μs |  1.02 |    0.04 |  6.7797 | 3.1780 | 0.2119 |  55.82 KB |        1.00 |
|                      |            |                 |           |          |          |       |         |         |        |        |           |             |
| **UnnamedExplicitAsync** | **Explicit**   | **Scoped**          |  **67.05 μs** | **1.006 μs** | **1.197 μs** |  **1.00** |    **0.02** |  **7.1121** | **3.2328** | **0.2155** |  **58.63 KB** |        **1.00** |
| NamedExplicitAsync   | Explicit   | Scoped          |  67.22 μs | 1.004 μs | 1.270 μs |  1.00 |    0.03 |  7.1942 | 3.3723 | 0.2248 |  58.63 KB |        1.00 |
|                      |            |                 |           |          |          |       |         |         |        |        |           |             |
| **UnnamedSingleAsync**   | **Single**     | **Singleton**       | **211.05 μs** | **2.212 μs** | **2.876 μs** |  **1.00** |    **0.02** | **10.8173** | **3.6058** | **1.2019** |  **90.56 KB** |        **1.00** |
| NamedSingleAsync     | Single     | Singleton       | 209.41 μs | 6.457 μs | 8.166 μs |  0.99 |    0.04 | 10.9890 | 4.8077 | 0.6868 |  90.44 KB |        1.00 |
|                      |            |                 |           |          |          |       |         |         |        |        |           |             |
| **UnnamedSingleAsync**   | **Single**     | **Scoped**          | **212.61 μs** | **1.886 μs** | **2.453 μs** |  **1.00** |    **0.02** | **11.1607** | **4.4643** | **1.1161** |   **95.8 KB** |        **1.00** |
| NamedSingleAsync     | Single     | Scoped          | 214.21 μs | 3.238 μs | 4.210 μs |  1.01 |    0.02 | 10.9649 | 4.3860 | 1.0965 |  95.43 KB |        1.00 |
