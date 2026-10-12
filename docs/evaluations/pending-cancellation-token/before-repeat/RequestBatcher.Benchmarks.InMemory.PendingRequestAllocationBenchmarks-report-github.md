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
| &#39;Complete one pending request&#39; | False       | 26.03 ns | 0.194 ns | 0.102 ns |    1 | 0.0191 |     160 B |
| &#39;Complete one pending request&#39; | True        | 47.29 ns | 0.300 ns | 0.157 ns |    2 | 0.0191 |     160 B |
