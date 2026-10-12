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
| &#39;Shared batch completion&#39; | 128          |    503.1 ns |   5.63 ns |   2.95 ns |    1 | 0.0172 |     144 B |
| &#39;Shared batch completion&#39; | 1024         |  3,833.0 ns |  45.55 ns |  23.82 ns |    2 | 0.0153 |     144 B |
| &#39;Shared batch completion&#39; | 8192         | 30,644.5 ns | 427.84 ns | 223.77 ns |    3 |      - |     144 B |
