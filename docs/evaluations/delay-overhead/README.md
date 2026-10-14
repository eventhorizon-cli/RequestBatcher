# 内存下游调用开销

使用 `Task.Delay(50)` 模拟下游耗时，对比有限并发的直接调用和 RequestBatcher 原生批量提交。
每波逻辑请求数为 `BatchSize × MaxConcurrency × 2`，两条路径使用相同输入、下游实现和并发上限。
直接路径用固定数量的异步 worker 领取批次；库使用 Scoped Handler，每波调用一次 `ExecuteAsync`。

## 结果

2026-10-14，macOS 27.0.1 / Arm64，.NET SDK 10.0.100、运行时 8.0.25、
BenchmarkDotNet 0.15.8，ShortRun（1 次启动、3 次预热、3 次测量），MemoryDiagnoser。
测量版本为 `9f48290`；之后的 API 改名不改变实现。

| BatchSize | MaxConcurrency | PartitionCount | 每波请求数 | 直接耗时 | 库耗时 | 均值比率 | 直接分配/请求 | 库分配/请求 | 额外分配/请求 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 1 | 4 | 1 | 8 | 102.062 ms | 102.195 ms | 1.0013 | 423.00 B | 1,659.80 B | +1,236.80 B |
| 1 | 16 | 1 | 32 | 101.985 ms | 101.802 ms | 0.9982 | 396.75 B | 1,502.10 B | +1,105.35 B |
| 100 | 4 | 8 | 800 | 102.135 ms | 102.447 ms | 1.0031 | 4.23 B | 174.57 B | +170.34 B |
| 100 | 16 | 8 | 3,200 | 102.063 ms | 103.318 ms | 1.0123 | 3.97 B | 176.79 B | +172.83 B |

`BatchSize=1` 不启用分区键，严格对照相同的下游调用次数；`BatchSize=100` 使用 `request % 8` 分区。
**本次两条路径的实际下游调用数完全相同**：并发 4 时每波 8 次，并发 16 时每波 32 次。
批量场景均为满批，峰值并发均达到配置值且未超限；每波逐 ID 验证恰好处理一次。

**批量场景的额外分配约为每请求 170–173 B，可作为后续优化基线。**
`BatchSize=1` 时，每个请求承担一次完整 Handler 调度和 DI scope，额外分配约 1.1–1.2 KB。
两组的分区配置也不同，不能仅凭此表精确拆出每批固定分配与每请求分配。

## 口径与局限

DI 容器、输入数组、验证数组和预热在计时区外；每波验证在计时区内，两条路径使用相同实现。
分配涵盖 Task、Delay、调度、scope、完成通知等整个 pipeline；每请求分配由每波托管分配除以逻辑请求数得到。
它不是存活内存或峰值内存，也未单独定位 TCS 等对象的占比。

时间是整波完成时间。**ShortRun 的 99.9% 置信区间重叠，无法确认小幅耗时差具有统计显著性**；
负数不表示库更快。50ms 定时器噪声会掩盖微小开销。
这是一组闭环突发负载，模拟 README 的延迟与并发预算，不验证固定 QPS 下的容量或尾延迟。

直接路径按连续 ID 切片，库按键路由；下游不依赖顺序、不去重。
机会式合并不保证满批，复测若实际批次数不同，不应把耗时差全部归因于框架开销。

## 复现

```bash
dotnet run --project tests/RequestBatcher.Benchmarks/RequestBatcher.Benchmarks.csproj \
  --configuration Release -- \
  --filter '*DelayDownstreamOverheadBenchmarks*' \
  --artifacts /private/tmp/requestbatcher-delay-overhead-artifacts
```

控制台输出每条路径实际调用数范围、平均有效批大小和峰值并发。
