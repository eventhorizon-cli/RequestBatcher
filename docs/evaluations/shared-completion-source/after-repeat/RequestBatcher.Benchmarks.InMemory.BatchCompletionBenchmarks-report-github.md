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
| &#39;Shared batch completion&#39; | 128          |    492.1 ns |   7.33 ns |   3.83 ns |    1 | 0.0172 |     144 B |
| &#39;Shared batch completion&#39; | 1024         |  3,791.5 ns |  27.19 ns |  14.22 ns |    2 | 0.0153 |     144 B |
| &#39;Shared batch completion&#39; | 8192         | 30,026.9 ns | 649.58 ns | 339.74 ns |    3 |      - |     144 B |
