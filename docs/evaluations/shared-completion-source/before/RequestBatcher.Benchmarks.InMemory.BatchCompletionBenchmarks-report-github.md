```

BenchmarkDotNet v0.15.8, macOS 27.0.1 (26A434) [Darwin 27.0.0]
Unknown processor
.NET SDK 10.0.100
  [Host]   : .NET 8.0.25 (8.0.25, 8.0.2526.11203), Arm64 RyuJIT armv8.0-a
  InMemory : .NET 8.0.25 (8.0.25, 8.0.2526.11203), Arm64 RyuJIT armv8.0-a

Job=InMemory  IterationCount=8  LaunchCount=1  
RunStrategy=Throughput  WarmupCount=3  

```
| Method                    | RequestCount | Mean        | Error     | StdDev    | Rank | Gen0   | Allocated |
|-------------------------- |------------- |------------:|----------:|----------:|-----:|-------:|----------:|
| &#39;Shared batch completion&#39; | 128          |    485.9 ns |   8.67 ns |   3.85 ns |    1 | 0.0200 |     168 B |
| &#39;Shared batch completion&#39; | 1024         |  3,717.9 ns |  66.70 ns |  34.89 ns |    2 | 0.0191 |     168 B |
| &#39;Shared batch completion&#39; | 8192         | 29,621.1 ns | 695.81 ns | 363.92 ns |    3 |      - |     168 B |
