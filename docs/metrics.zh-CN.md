# RequestBatcher Metrics

本文介绍四个 Histogram 和 backlog Gauge 的记录边界、标签、采集及查询方式。另有[英文指南](metrics.md)。

## 注册

只采集 Metrics 时，可直接注册 Meter：

```csharp
builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics => metrics.AddMeter("RequestBatcher"));
```

也可安装 `RequestBatcher.OpenTelemetry` 并使用
`.WithMetrics(metrics => metrics.AddRequestBatcherInstrumentation())`。两种方式都会注册 Meter，并使用 core 提供的
大小 Histogram 建议桶边界（Advice）。应先完成所有 Batcher 注册，再构建服务容器。首次解析 Batcher 时，共享的 Metrics 服务
根据已注册的最大 `BatchSize` 创建固定的 Advice，无需解析其他 Batcher 或 Handler。容器在 Batcher 排空后异步释放 Metrics 服务；
Metrics 会等待已开始的 Execute 测量完成记录，再释放 Meter。
不同容器可提供不同桶边界；不要将边界不兼容的指标合并到同一指标流中。
容器释放 Metrics 服务后，通过保留的 Batcher 引用发起调用也不会再记录指标。

## 指标

| 指标名称 | 类型 | 单位 | 含义 |
| --- | --- | --- | --- |
| `requestbatcher.submission.size` | `Histogram<long>` | `{request}` | 到达 RequestBatcher 接纳边界的每次提交所含请求数。 |
| `requestbatcher.batch.size` | `Histogram<long>` | `{request}` | 每个已完成 Handler 批次的请求数，包含失败尝试；分发前取消的请求不计。 |
| `requestbatcher.handle.duration` | `Histogram<double>` | `ms` | Handler 批次耗时，包含 Handler 作用域释放。 |
| `requestbatcher.execute.duration` | `Histogram<double>` | `ms` | 每次公开 `ExecuteAsync` 调用的端到端耗时，包含容量等待、排队、所有 Handler 批次及响应组装。 |
| `requestbatcher.backlog` | `ObservableGauge<long>` | `{request}` | 已入队、尚未拉取的请求项数近似值，不包含等待入队或正在处理的请求。 |

Histogram 的 `sum` 累加测量值，`count` 记录测量次数，bucket 描述分布；`sum / count` 得到平均值。OTLP 将 sum 和 count
作为同一个 Histogram 数据点中的字段导出。

`submission.size` 在提交到达接纳边界时记录一次，值为枚举后得到的请求项数，空集合记为 0。调用前已取消、Batcher 已停止、
集合参数为 null 或枚举失败的提交不计；接纳后的容量拒绝及后续失败仍计入。容量切片和 Handler 拆批不会重复记录。
`batch.size` 每个已完成 Handler 批次记录一次，包含失败尝试，分发前取消的请求不计。`handle.duration` 包含 Handler 执行和作用域
释放；`execute.duration` 衡量调用方可见的完整操作。计算 `execute.duration` P95 时筛选 `requestbatcher.outcome=success`，避免快速失败和取消的调用拉低延迟。

所有指标都包含请求类型短名（`requestbatcher.request.type`）和可选的 Batcher 名称（`requestbatcher.name`）。同名类型共享请求类型维度，
需要区分时可配置不同的 Batcher 名称。`submission.size` 增加 `requestbatcher.submission.kind`（`single` 或 `multi`）标签，不带 outcome。
`execute.duration` 记录 `requestbatcher.submission.kind` 和 `requestbatcher.outcome`（`success`、`error` 或 `canceled`）。订阅开启时，空集合、
失败和取消的调用各记录一次；同步校验并抛出异常的 null 集合参数不计量。outcome 按返回 Task 的最终状态确定，而非按异常类型判断。
委托提交的耗时包含 Handler 绑定，绑定失败记为 `error`；委托扩展方法的参数校验和 Batcher 类型检查发生在计量之前。
不记录请求内容、租户值或 Partition Key。`single` 表示单条请求重载，`multi` 表示集合重载，即使集合为空或只有一项也属于 `multi`；
这些标签不表示实际形成了几个 Handler 批次。

## 队列积压

`requestbatcher.backlog` 在采集器触发采集时读取 Consumer 快照，覆盖该 Batcher 的所有分区。等待入队的请求和已经拉取的
请求不计入；已经取消但仍留在队列里的项，等拉取并跳过后才不再计入。成功读取时报告当前近似值，
包括零；统计暂不可用时跳过本次观测，不用零代替。

采集时机由应用的采集器决定，支持 OpenTelemetry 周期采集、拉取式导出和原生 `MeterListener`，不另开定时器。
采集间隔内的短暂峰值可能看不到。Gauge 沿用请求类型和可选的 Batcher 名称标签，不增加提交类型、结果或分区标签。
消费循环负责注册和注销该 Batcher 的 backlog 观测源：正常排空期间仍可采集，循环退出时注销，包括因异常退出的情况；
已经开始的采集可能在注销过程中完成。

这个 Gauge 没有桶，也不是累计总数，直接查看它的值即可，不需要使用 `rate`。
OTLP 保留名称 `requestbatcher.backlog`；项目示例中的 Prometheus 查询为 `requestbatcher_backlog`。

## 导出名称与查询

通过 OTLP 导出时保留 `requestbatcher.execute.duration`、`requestbatcher.submission.kind` 这类点分名称。项目示例中的 Prometheus 转换会生成
`requestbatcher_submission_size_sum`、`_count` 和 `_bucket`（`batch.size` 同理），以及
`requestbatcher_execute_duration_milliseconds_sum`、`_count` 和 `_bucket`（`handle.duration` 同理）。标签键变成下划线形式，标签值不变；
不同导出端的名称转换和直方图表示可能不同。

查询成功的集合提交 P95（单位为 ms）：

```promql
histogram_quantile(0.95,
  sum by (le, requestbatcher_name, requestbatcher_request_type) (
    rate(requestbatcher_execute_duration_milliseconds_bucket{
      requestbatcher_submission_kind="multi", requestbatcher_outcome="success"
    }[5m])
  )
)
```

时间范围、请求类型和 Batcher 名称相同时（对提交类型求和），`submission.size` 的 sum 是已接纳请求项数，`batch.size` 的 sum 是 Handler
已消费请求项数。`batch.size` 和 `handle.duration` 的 count 是已完成 Handler 批次数。`execute.duration` 的 count 是已完成公开调用数，包含接纳前
被拒绝的调用，因此不是已提交请求项数。以下查询按每组标签分别计算。

| 观测值 | Prometheus 表达式 |
| --- | --- |
| 最近一分钟已接纳请求项数（估算） | `increase(requestbatcher_submission_size_sum[1m])` |
| 最近一分钟已接纳提交次数（估算） | `increase(requestbatcher_submission_size_count[1m])` |
| 已接纳提交数/秒 | `rate(requestbatcher_submission_size_count[5m])` |
| Handler 已消费请求项数/秒 | `rate(requestbatcher_batch_size_sum[5m])` |
| 已完成 Handler 批次数/秒 | `rate(requestbatcher_batch_size_count[5m])` |
| 已完成公开调用数/秒，包含接纳前被拒绝的调用 | `rate(requestbatcher_execute_duration_milliseconds_count[5m])` |
| 每次提交或 Handler 批次的平均请求数 | `rate(<size>_sum[5m]) / rate(<size>_count[5m])` |

P95 大小可用 `histogram_quantile(0.95, sum by (le, requestbatcher_name, requestbatcher_request_type) (rate(<size>_bucket[5m])))` 估算；
`<size>` 替换为 `requestbatcher_submission_size` 或 `requestbatcher_batch_size`。耗时平均值可用 Histogram 的 sum 除以 count，或用对应 Prometheus
`_sum` 和 `_count` 的 rate 相除。

旧 Counter 查询可按后缀迁移：`requestbatcher_requests_submitted_total` 对应 `requestbatcher_submission_size_sum`，
`requestbatcher_requests_processed_total` 对应 `requestbatcher_batch_size_sum`，`requestbatcher_batches_processed_total` 对应
`requestbatcher_batch_size_count`（或 `requestbatcher_handle_duration_milliseconds_count`）。旧批次耗时查询中的
`requestbatcher_batch_duration_milliseconds_*` 改为 `requestbatcher_handle_duration_milliseconds_*`。

## 请求数量的分桶规则

桶配置取同一服务集合中最大的 `BatchSize`，记为 B，与 Batcher 数量无关。`batch.size` 使用 `0`、`1` 和
`ceil(B * i / 22)`（`i = 1..22`）作为边界，并排序去重；B <= 22 时每个整数大小都有对应边界。`submission.size` 独立使用
`0`、`1` 和 `ceil(B * i / 17)`（`i = 1..17`）作为边界，再加入 `2B`、`4B`、`8B`、`16B` 和 `32B`；B <= 17 时每个整数大小都有对应边界。
若两个 Batcher 的 `BatchSize` 分别为 40 和 120，则 B 取 120：`batch.size` 使用 `ceil(120 * i / 22)`，`submission.size` 使用 `ceil(120 * i / 17)`，两者都包含 `0` 和 `1`，
且 `submission.size` 还增加 `240, 480, 960, 1920, 3840`。

SDK 为每个 Histogram 添加 `+Inf`。每个大小 Histogram 最多有 25 个 classic Prometheus bucket 序列；加上 `_sum` 和 `_count` 后，每组标签最多 27 个序列。
这个上限不随 Batcher 数量增加，但新增标签组仍会增加总序列数。`submission.size` 中大于 `32B` 的值落入 `+Inf`；`batch.size` 的最大有限边界为 B。
所有观测值仍会计入 sum、count 和平均值；分位数由桶估算，因此较粗，且无法解析 `submission.size` 中 `32B` 以上的尾部。

只有大小 Histogram 提供这些 Advice。`handle.duration` 和 `execute.duration` 保持 OpenTelemetry SDK 默认边界：
`0, 5, 10, 25, 50, 75, 100, 250, 500, 750, 1000, 2500, 5000, 7500, 10000 ms`，另有溢出桶。聚合多个实例的 bucket 计算分位数时应使用相同边界。
直接注册 `AddMeter("RequestBatcher")` 也会使用这些 Advice；应用通过 `AddView` 设置的边界优先。Advice 仅为建议，其他采集端可忽略。
原生 `MeterListener` 可读取 `Histogram<long>.Advice.HistogramBucketBoundaries`，但接收的仍是原始测量值，需要自行聚合。

## MeterListener

自定义 `System.Diagnostics.Metrics.MeterListener` 时，需要显式订阅想采集的指标：

```csharp
listener.InstrumentPublished = (instrument, meterListener) =>
{
    if (instrument.Meter.Name == "RequestBatcher")
    {
        meterListener.EnableMeasurementEvents(instrument);
    }
};
```

- 指标发现和注册回调都不等于订阅。每个 Listener 对要采集的每个指标调用一次 `EnableMeasurementEvents`；需要时按 `instrument.Name` 筛选。
- 为两个大小 Histogram 注册 `long` 回调，为两个耗时 Histogram 注册 `double` 回调。提交请求前调用 `Start()`，并让 Listener 存活到 Batcher 关闭完成；此前的测量不会补发。
- backlog Gauge 同样使用 `long` 回调。启用后调用 `RecordObservableInstruments()` 读取当前值；仅调用 `Start()` 不会定时采集 Observable 指标。
- 回调同步执行，也可能并发调用。保持轻量、线程安全，避免阻塞 I/O 和异常向外传播；回调返回后仍要使用的标签需要复制。聚合和导出由自定义 Listener 实现。
