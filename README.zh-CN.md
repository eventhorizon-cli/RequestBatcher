# RequestBatcher

[English](README.md) | **简体中文**

RequestBatcher 是一个进程内 .NET SDK，它把并发提交的单个请求交给 handler 批量处理。调用方只需使用简单
的异步接口并等待自己的 `Task`；handler 收到 `IReadOnlyList<TRequest>` 后，可以通过一次操作完成批量写入、
查询或下游调用。

```text
调用方：ProcessAsync(TRequest)                 -> Task
Handler：HandleAsync(IReadOnlyList<TRequest>)  -> ValueTask
```

## 功能

- 透明攒批：调用方逐个提交请求，不需要创建或管理批次。
- 机会式聚合：已有请求不会为了凑满一批而等待；当前已排队的请求会被一起处理，单批不超过 `BatchSize`。
- 可控并发：通过 `MaxConcurrency` 限制同时执行的 handler 数量。
- Partition Key：相同数值或字符串 key 会进入同一 partition，避免并发处理同一实体。
- 每请求结果：每个调用方都能收到自己请求的成功、异常或取消结果。
- 背压：限制待处理请求总数，容量耗尽时可以异步等待或立即拒绝。
- 复用应用基础设施：handler 生命周期明确，BufferQueue 保持内部实现，不创建嵌套 `ServiceProvider`。
- 优雅停止：协调器停止前会排空已经接受的请求。

## 适用场景

RequestBatcher 适合这样的进程内调用链路：并发调用方在一段时间内快于下游依赖，而下游可以从批量操作中
获益。这和进程内缓冲解决的生产/消费速度不一致问题相同，但 RequestBatcher 把攒批隐藏在单请求 API 后面。

- **数据库写入：** 将独立写入合并为批量 `INSERT`、`UPDATE`、`UPSERT` 或事务处理，减少往返和提交开销。
- **缓存与批量 API：** 将多个缓存更新、缓存读取、HTTP/RPC 提交或事件发布，交给已支持多条数据的下游操作。
- **突发流量平滑与背压：** 通过 `MaxPendingRequests`、`FullMode` 和 `MaxConcurrency` 限制内存中的待处理工作、
  数据库并发或外部 API 并发。
- **按实体串行：** 订单、商品、账户、库存、设备等场景可使用 `UsePartitionKey`，保证同一实体不并发处理，
  同时让无关实体并行。
- **高频状态合并：** 在单批内保留最新版本，再通过存储层版本检查、幂等键、唯一约束或事务，保证跨批次更新
  不会写入旧状态。

RequestBatcher 不是持久化队列，也不能替代消息队列。它不会持久化待处理请求、在进程崩溃后恢复、自动重试
失败的 handler 调用，或在 `MaxConcurrency > 1` 时提供全局顺序。它只返回完成状态，不提供 `Task<TResult>`；
批量读取若要返回业务数据，应由请求对象携带结果容器，或由 handler 更新应用状态。

不能把必须与调用方事务原子提交或回滚的操作交给 RequestBatcher。例如下单写入、扣减库存、扣款和审计记录
若共同构成一个业务事务，就必须留在该事务中；将其中一步放入 RequestBatcher 会打破原子边界，并使它在调用方
工作之后才执行。RequestBatcher 只适合独立的后续工作，或者先通过显式事务 Outbox 记录后再处理。

## 快速开始

先定义请求以及负责处理一个批次的 handler：

```csharp
public sealed record OrderWriteRequest(long OrderId, decimal Amount);

public interface IOrderStore
{
    ValueTask WriteBatchAsync(
        IReadOnlyList<OrderWriteRequest> requests,
        CancellationToken cancellationToken);
}

public sealed class OrderWriteBatchHandler(IOrderStore store)
    : IRequestBatchHandler<OrderWriteRequest>
{
    public ValueTask HandleAsync(
        IReadOnlyList<OrderWriteRequest> requests,
        CancellationToken cancellationToken = default) =>
        store.WriteBatchAsync(requests, cancellationToken);
}
```

在同一次调用中注册 handler 和 batcher。handler 生命周期是必填参数：

```csharp
services.AddRequestBatcher<OrderWriteRequest, OrderWriteBatchHandler>(
    ServiceLifetime.Scoped,
    options =>
    {
        options.BatchSize = 256;
        options.MaxConcurrency = 4;
        options.MaxPendingRequests = 10_000;
        options.UsePartitionKey(request => request.OrderId);
    });
```

调用方解析 `IRequestBatcher<TRequest>`，并逐个提交请求：

```csharp
var batcher = serviceProvider.GetRequiredService<IRequestBatcher<OrderWriteRequest>>();

await batcher.ProcessAsync(
    new OrderWriteRequest(OrderId: 42, Amount: 99.50m),
    cancellationToken);
```

返回的 `Task` 只会在该请求所属批次处理完成后结束。调用方不需要知道请求进入了哪个批次或 partition。

如果不需要单独定义 handler 类型，也可以直接注册委托：

```csharp
services.AddRequestBatcher<OrderWriteRequest>(
    (batch, cancellationToken) =>
        database.WriteOrdersAsync(batch, cancellationToken),
    ServiceLifetime.Singleton,
    options => options.BatchSize = 256);
```

## 架构

![RequestBatcher 架构](docs/assets/request-batcher-architecture.png)

调用方只依赖 `IRequestBatcher<TRequest>`。协调器拥有内部 Memory topic 及其 partitions，以批次调用已注册的
handler，再根据该批次的处理结果完成每个已接受调用方的 `Task`。实线表示请求与批处理流，虚线表示完成结果流。

## 批次如何形成

RequestBatcher 不会为了固定攒批窗口而延迟第一个请求。当某个 partition 可以开始处理时，它会从当前已排队的
请求中提取最多 `BatchSize` 个，然后交给 handler。低流量下可能形成单请求批次，并发流量较高时则会自然形成
更大的批次。

`BatchSize` 是单批上限，不是最低数量。调用方不需要配合特定的提交时机，也不会因为等待凑满一批而引入
固定延迟。

## 配置

| 选项 | 默认值 | 行为 |
| --- | ---: | --- |
| `BatchSize` | `128` | 单次 handler 调用最多包含的请求数。 |
| `MaxConcurrency` | `1` | handler 最大并发调用数，同时也是处理 partition 数量。 |
| `MaxPendingRequests` | `8192` | 已接受但尚未完成的请求数量上限。 |
| `FullMode` | `Wait` | 默认异步等待容量；`Fail` 会立即抛出 `RequestBatchQueueFullException`。 |
| `UsePartitionKey(...)` | 未配置 | 默认轮询路由；配置 selector 后，相同 key 会进入同一 partition。 |

`MaxConcurrency = 1` 时保持全局 FIFO。提高该值后，只保证各 partition 内有序，handler 也可能被并发调用。

## Partition Key 与重复请求合并

同一实体的请求不能同时处理时，可以配置 partition key：

```csharp
options.MaxConcurrency = 4;
options.UsePartitionKey(request => request.OrderId);
```

目前支持值为有限整数的数值 key 和非 `null` 字符串 key。selector 应当是确定且无副作用的函数。

- 相同 key 会进入同一 partition，并保持写入该 partition 后的顺序。
- 并发调用在写入 partition 前没有额外的先后保证。
- 不同 key 可能共享 partition，并不是每个 key 独占一个 partition。
- Partition Key 不保证同一个 key 的所有请求进入同一批，也不会自动去重。

`UsePartitionKey` 是按实体安全合并请求的路由前提，不是自动合并操作。例如多个 `PriceUpdate` 请求具有相同
`ProductId` 时，handler 可以在单批中按产品分组，只持久化最高版本。相同产品不会并发处理，不同产品仍可
使用不同 partition 并行。最终写入成功后，该批中所有调用方的 `Task` 都会成功完成，包括合并时被较新更新
覆盖的请求。

可运行的[重复数据合并示例](samples/RequestBatcher.Deduplication)展示了保证正确性所需的两层处理：handler 在
单批内按产品保留最高版本，存储层再拒绝跨批次到达的旧版本。存储层检查不能省略，因为 Partition Key 不会让
所有重复请求都进入同一个批次。

## 结果与失败语义

- handler 成功时，同一批的所有请求成功完成。
- handler 抛出异常时，同一批的所有请求都会收到原始异常；该异常按批次记录一次日志。
- 调用方取消只会移除尚未交给 handler 的请求。批次开始处理后，调用方会收到真实处理结果，而不是语义
  不明确的取消结果。
- `StopAsync` 和 `DisposeAsync` 会先停止接收新请求，再排空所有已经接受的请求。

RequestBatcher 是进程内调度组件，不会持久化待处理请求，也无法在进程崩溃后恢复。如果请求不能丢失，
应在 handler 中使用数据库事务、WAL 或可靠消息系统建立持久化边界。

## 依赖注入与日志

`AddRequestBatcher` 会把 handler 和内部 BufferQueue topic 注册到应用现有的 `IServiceCollection`。调用方不
需要单独安装或配置 BufferQueue，RequestBatcher 也不会创建自己的 Service Provider。

`Scoped` 和 `Transient` handler 会在每个批次的异步作用域中解析一次。`Singleton` handler 会跨批次复用，
当 `MaxConcurrency > 1` 时必须是线程安全的。

日志使用应用现有的 `Microsoft.Extensions.Logging` 管道，类别为 `RequestBatchCoordinator<TRequest>`。
handler 和入队异常会保留原始 exception，日志不会写入请求 payload。应用未注册 logger 时使用
`NullLogger`。

## 仓库内容

- [重复数据合并示例](samples/RequestBatcher.Deduplication)
- [单元测试](tests/RequestBatcher.Tests)
- [PostgreSQL 与 Redis Benchmark](tests/RequestBatcher.Benchmarks)

```bash
dotnet build RequestBatcher.slnx --configuration Release
dotnet test tests/RequestBatcher.Tests/RequestBatcher.Tests.csproj --configuration Release
```

## License

RequestBatcher 使用 [MIT License](LICENSE)。
