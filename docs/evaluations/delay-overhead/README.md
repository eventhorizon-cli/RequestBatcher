# 内存下游调用开销

使用 `Task.Delay(50)` 模拟下游耗时，比较直接调用和库的单条、原生批量调用。
配置为 Scoped Handler、`BatchSize=100`、`partitionCount=8`、`MaxConcurrency=16`；
队列容量足以容纳全部请求，ID 各不相同、不去重，每波验证所有 ID 恰好完成一次。

## 流量模型

**默认持续输入：8 个独立调用方，合计目标 16,000 items/s，约 1 秒提交 16,000 项。**
每个调用方的单条模式为 2,000 次/s，批量模式为每次 100 项、20 次/s；首个调用按周期错开。
使用共同 Stopwatch 起点和绝对到达时间，只等计划时间，**不等上次请求完成再提交**。
迟到则赶进度，发完后统一等待完成；不加屏障凑满 Handler 批次。

两条直接调用主路径均不限制并发。直接批量并发 16 仅作参考，持续模式使用共享 `SemaphoreSlim`，
门控和包装 Task 的分配也计入结果。库的批量请求按项分区，可能拆分并与其他提交合并。

`REQUEST_BATCHER_TRAFFIC=burst` 单独运行 16,000 / 64,000 项突发输入：单条立即逐项发起，
直接批量按 100 项发起，库批量一次提交整波；有界直接路径用 16 个固定 worker。
**持续模式每次批量提交 100 项，突发模式库一次提交整波，两者不能直接当作相同 API 负载横比。**

## 持续输入结果

2026-10-14，macOS 27.0.1 / Arm64，.NET SDK 10.0.100、运行时 8.0.25，BenchmarkDotNet 0.15.8。
Monitoring + MemoryDiagnoser：1 次启动、1 次预热、3 次测量，每次一波。
下表为本地非池化优化后的结果；持续模式 5 个组合含自动构建共 **39.90 秒**，突发模式 10 个组合共 **57.91 秒**。
本次优化结果对应生产代码 `251e378`。

| 路径 | 整波耗时 | 分配/项 | 提交延迟 p95 | 下游调用数/波 | 峰值并发 |
| --- | --- | --- | --- | --- | --- |
| 直接单条，不限并发 | 1.054 s | 439.80 B | 51.19 ms | 16,000 | 1,041 |
| 直接批量，不限并发 | 1.045 s | 6.42 B | 52.01 ms | 160 | 10 |
| 直接批量，并发 16 | 1.047 s | 7.81 B | 51.83 ms | 160 | 16 |
| 库单条 | 1.064 s | 357.05 B | 95.35 ms | 328 | 16 |
| 库每次批量 100 项 | 1.078 s | 199.90 B | 92.04 ms | 328 | 16 |

各路径的平均发起时长为 0.994–1.001 秒，对应约 15,981–16,091 items/s。
库单条、批量的平均有效批大小均为 48.78；直接批量为 100。
**已有下游批量调用只需 160 次，经过库后本轮观测为 328 次；实际调用次数会随调度变化，并不保证调用更少或响应更快。**

## 非池化优化前后

以 0.1.0 为基线，没有请求被跳过时复用 BufferQueue 的独立请求快照；需要过滤时使用 `List`，由集合自身维护有效数量。
保留独立 Handler 输入数组，不池化请求、Task 或缓冲区。

| 库调用方式 | 分配/项：优化前 → 后 | 减少/项 |
| --- | --- | --- |
| 持续输入，单条，16,000 项 | 369.19 → 357.05 B | 12.14 B |
| 持续输入，每次 100 项，16,000 项 | 209.20 → 199.90 B | 9.30 B |
| 突发，单条，64,000 项 | 275.99 → 267.53 B | 8.46 B |
| 突发，一次整波，64,000 项 | 176.28 → 167.74 B | 8.54 B |

持续输入的实际批次数和产生流量的分配会随调度变化，分配下降不能全归因于去掉列表；
突发单条也有 641–656 次调用。
**突发整波前后均为 640 次满批，更适合验证固定工作量下的分配收益：约减少 4.8%。**
短测的耗时置信区间重叠，不据此断言执行速度提升。

## 统计口径与局限

输入数组和 DI 容器在测量外；时间与分配包含产生流量的 `Task.Delay`、Task 数组、等待和验证。
单条与批量应分别比较各自的直接调用和库调用；每项托管分配不是存活内存或峰值内存。
整波耗时包含约 1 秒计划输入时间，不能代替单次请求延迟或框架 CPU 开销。

p95 来自测量外独立重放的一波，按提交统计：单条 1 项，持续批量 100 项，突发库批量整波。
只在该诊断波添加延迟观察 Task，不影响 MemoryDiagnoser；一波分位数仅用于观察排队。
下游调用数范围、平均批大小和峰值取预热、测量及内存诊断波，不包含延迟诊断波。

毫秒定时器无法精确形成 0.5ms 间隔；等待向上取整且迟到赶进度，实际流量可能短暂聚集。
**未模拟真实网络、序列化、连接池、下游饱和或限流，也未覆盖超时取消、返回值映射。**
限流排队与不满批造成的额外下游调用，不能全部算作库的执行开销。

## 复现

默认持续输入：

```bash
NUGET_HTTP_CACHE_PATH=/private/tmp/requestbatcher-delay-overhead-nuget-cache \
dotnet run --project tests/RequestBatcher.Benchmarks/RequestBatcher.Benchmarks.csproj \
  --configuration Release -- --filter '*DelayDownstreamOverheadBenchmarks*' \
  --artifacts /private/tmp/requestbatcher-delay-overhead-steady-artifacts
```

单独运行突发输入：

```bash
REQUEST_BATCHER_TRAFFIC=burst \
NUGET_HTTP_CACHE_PATH=/private/tmp/requestbatcher-delay-overhead-nuget-cache \
dotnet run --project tests/RequestBatcher.Benchmarks/RequestBatcher.Benchmarks.csproj \
  --configuration Release -- --filter '*DelayDownstreamOverheadBenchmarks*' \
  --artifacts /private/tmp/requestbatcher-delay-overhead-burst-artifacts
```

原始日志和完整导出仅保留在 `/private/tmp`。

## Observability

This historical delay and overhead evaluation does not measure the optional `RequestBatcher.OpenTelemetry`
instrumentation. See the [observability design](../../observability-design.md) for the production tracing contract.
