```

BenchmarkDotNet v0.15.8, macOS 27.0.1 (26A434) [Darwin 27.0.0]
Unknown processor
.NET SDK 10.0.100
  [Host]                   : .NET 8.0.25 (8.0.25, 8.0.2526.11203), Arm64 RyuJIT armv8.0-a
  PendingRequestAllocation : .NET 8.0.25 (8.0.25, 8.0.2526.11203), Arm64 RyuJIT armv8.0-a

Job=PendingRequestAllocation  IterationCount=8  LaunchCount=1  
RunStrategy=Throughput  WarmupCount=3  

```
| Method                         | Cancellable | Mean     | Error    | StdDev   | Rank | Gen0   | Allocated |
|------------------------------- |------------ |---------:|---------:|---------:|-----:|-------:|----------:|
| &#39;Complete one pending request&#39; | False       | 24.55 ns | 0.165 ns | 0.086 ns |    1 | 0.0191 |     160 B |
| &#39;Complete one pending request&#39; | True        | 43.11 ns | 0.327 ns | 0.171 ns |    2 | 0.0191 |     160 B |
