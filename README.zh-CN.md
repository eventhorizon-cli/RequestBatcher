# RequestBatcher

[![NuGet](https://img.shields.io/nuget/v/RequestBatcher.svg)](https://www.nuget.org/packages/RequestBatcher)
[![Build](https://github.com/eventhorizon-cli/RequestBatcher/actions/workflows/dotnet-build.yml/badge.svg)](https://github.com/eventhorizon-cli/RequestBatcher/actions/workflows/dotnet-build.yml)
[![Codecov](https://codecov.io/gh/eventhorizon-cli/RequestBatcher/graph/badge.svg)](https://codecov.io/gh/eventhorizon-cli/RequestBatcher)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

[English](README.md) | **简体中文**

RequestBatcher 在进程内把并发请求交给 handler 攒批处理，调用方不需要协调批次。调用方可以提交一个
`TRequest`，也可以提交已有的请求序列，并等待一个 `Task`；handler 接收 `IReadOnlyList<TRequest>`，通过一次
操作完成该批次的写入、查询或下游调用。

```text
调用方：ProcessAsync(TRequest)                       -> Task
        ProcessAsync(IEnumerable<TRequest>)          -> Task
Handler：HandleAsync(IReadOnlyList<TRequest>)        -> ValueTask
```

## 功能

- **透明攒批：** 调用方逐个提交请求，不需要创建或管理批次。
- **显式批量提交：** 调用方已经持有多个请求时，可以通过一次生产操作入队，并等待同一个完成任务。
- **机会式聚合：** 从已经排队的请求中提取最多 `BatchSize` 个，不会为了凑满一批而固定延迟第一个请求。
- **并发限制：** `MaxConcurrency` 控制同时执行的 handler 数量。
- **分区路由：** 数值或字符串 key 相同的请求会进入同一处理分区。
- **每请求完成状态：** 每个调用方都能收到自己请求的成功、异常或取消结果。
- **背压：** 限制待处理请求总数，容量耗尽时可以异步等待或立即拒绝。
- **复用应用基础设施：** 使用应用现有的依赖注入和日志配置；BufferQueue 保持为内部实现，不创建嵌套
  `ServiceProvider`。
- **优雅停止：** 停止处理前会排空已经接受的请求。

## 安装

```bash
dotnet add package RequestBatcher
```

## 快速开始

先定义请求和批处理 handler：

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

在同一次调用中注册 handler 和 batcher，并明确指定 handler 生命周期：

```csharp
services.AddRequestBatcher<OrderWriteRequest, OrderWriteBatchHandler>(
    ServiceLifetime.Scoped,
    options =>
    {
        options.BatchSize = 256;
        options.MaxConcurrency = 4;
        options.MaxPendingRequests = 10_000;
        // Optional; requests use round-robin routing when this is omitted.
        options.UsePartitionKey(request => request.OrderId);
    });
```

解析 `IRequestBatcher<TRequest>`，然后逐个提交请求：

```csharp
var batcher = serviceProvider.GetRequiredService<IRequestBatcher<OrderWriteRequest>>();

await batcher.ProcessAsync(
    new OrderWriteRequest(OrderId: 42, Amount: 99.50m),
    cancellationToken);
```

返回的 `Task` 会在该请求所属批次处理完成后结束。调用方不需要知道请求进入了哪个批次或分区。

如果调用方已经持有一组请求，可以一次提交：

```csharp
await batcher.ProcessAsync(orderWriteRequests, cancellationToken);
```

`ProcessAsync` 的批量重载会先取得请求序列的快照，再把它作为一个整体准入。返回的 `Task` 会等待所有请求
完成，但这些请求仍可能按分区或 `BatchSize` 拆给多次 handler 调用；批量提交不会创建新的 handler 批次边界。

也可以直接注册委托，不必单独实现 `IRequestBatchHandler<TRequest>`：

```csharp
services.AddRequestBatcher<OrderWriteRequest>(
    (batch, cancellationToken) =>
        database.WriteOrdersAsync(batch, cancellationToken),
    ServiceLifetime.Singleton,
    options => options.BatchSize = 256);
```

## 适用场景

RequestBatcher 适合进程内的并发调用链路：调用方可能暂时快于下游，而下游已经能从一次处理多条数据中获益。

- **数据库写入：** 将相互独立的操作合并为批量 `INSERT`、`UPDATE`、`UPSERT` 或事务处理，减少往返和提交
  开销。
- **缓存与批量接口：** 当下游支持一次处理多条数据时，合并缓存读写、HTTP/RPC 请求或事件发布。
- **突发流量平滑：** 使用 `MaxPendingRequests`、`FullMode` 和 `MaxConcurrency` 限制排队量以及数据库或外部
  接口的并发量。
- **按业务 key 分流：** 按订单、商品、账户、库存或设备路由，使相同 key 共用一个分区，同时允许其他 key
  并行。
- **重复状态更新：** 在单批内合并更新，再通过版本检查、幂等键、唯一约束或事务保证跨批次处理的正确性。

## 不适用场景

- **需要持久化的任务：** 待处理请求只保存在内存中，进程退出后无法恢复。已经接受的任务不能丢失时，应使用
  数据库、WAL 或可靠消息队列。
- **属于调用方事务的操作：** 必须与调用方一起提交或回滚的操作应留在原事务内。独立的后续工作可以先写入
  Transactional Outbox，再交给 RequestBatcher 处理。
- **每次调用需要独立返回值：** 公共 API 通过 `Task` 返回完成状态，而不是 `Task<TResult>`。批量查询需要由
  请求对象携带结果容器，或由 handler 更新应用状态。
- **并行处理下的全局顺序：** 只有 `MaxConcurrency = 1` 才能保证全局 FIFO；提高并发后只保证分区内顺序。
- **自动重试或 exactly-once 副作用：** handler 失败会反馈给调用方，但不会自动重试。调用方可能重试时，
  handler 或存储层需要提供幂等保护。

## 批次如何形成

RequestBatcher 使用机会式攒批。当某个分区可以开始处理时，它会从已经排队的请求中提取最多 `BatchSize` 个，
然后交给 handler。低流量下可能形成单请求批次，并发流量较高时通常会自然形成更大的批次。

`BatchSize` 是单批上限，不是最低数量。RequestBatcher 不会为了等待满批而增加固定延迟，调用方也不需要配合
特定的提交时机。

批量重载只执行一次 BufferQueue 批量生产，但分区路由仍然逐项进行。因此，同一次提交的请求到达
handler 前，仍可能按分区和 `BatchSize` 被拆分。

## 配置

| 选项 | 默认值 | 行为 |
| --- | ---: | --- |
| `BatchSize` | `128` | 单次 handler 调用最多包含的请求数。 |
| `MaxConcurrency` | `1` | handler 最大并发调用数，同时也是处理分区数。 |
| `MaxPendingRequests` | `8192` | 已接受但尚未完成的请求数量上限，也是单次显式批量提交的数量上限。 |
| `FullMode` | `Wait` | 默认异步等待容量；`Fail` 返回 faulted `Task`，异常为 `RequestBatchQueueFullException`。 |
| `UsePartitionKey(...)` | 未配置 | 默认轮询路由；配置 selector 后，相同 key 会进入同一分区。 |

`MaxConcurrency = 1` 时按全局 FIFO 处理。提高该值后，handler 可能并发执行，此时只保证各分区内有序。

显式批量提交会整体预留容量。`Wait` 模式下，`ProcessAsync` 会等待整批都能容纳；`Fail` 模式会拒绝整批，不会只接受前半部分。
批次数量超过 `MaxPendingRequests` 时始终抛出 `RequestBatchQueueFullException`，`RequestedCount` 表示被拒绝的
请求数。

## Partition Key

`UsePartitionKey` 决定每个请求进入哪个处理分区。selector 从请求中取出稳定的业务 key；selector 结果相同的
请求会进入同一分区：

```csharp
options.MaxConcurrency = 4;
options.UsePartitionKey(request => request.OrderId);
```

key 可以是值为有限整数的数值，也可以是非 `null` 字符串。selector 应保持确定性、无副作用，并能安全地被
并发调用。

- 相同 key 会进入同一分区，并按写入该分区的顺序处理。
- 多个调用并发发生时，写入分区之前没有额外的先后保证。
- 不同 key 可能落入同一分区；key 与分区不是一一对应关系。
- Partition Key 只负责路由，不保证同一 key 的请求进入同一批，也不会自动去重。

### 重复请求合并示例

合并重复更新是分区路由的一种用法。假设多个 `PriceUpdate` 具有相同 `ProductId`，handler 可以在单个批次内
按产品分组，只写入版本最高的更新。同一产品的请求不会由多个 handler 并发处理，其他产品仍可在不同分区并行。
批次写入成功后，该批中的所有 `Task` 都会成功完成，包括被较新版本覆盖的请求。

可运行的[重复更新 Web API 示例](samples/RequestBatcher.Deduplication)使用 PostgreSQL，展示了这类处理需要的两层
保护：handler 在单批内保留每个产品的最高版本，再通过一次批量 upsert 写入；数据库拒绝跨批次到达的旧版本。
存储层检查不能省略，因为 Partition Key 不会让同一 key 的所有请求都进入同一个批次。

同一示例也展示了查询攒批。每个查询请求携带自己的结果，query handler 在单批内对 `ProductId` 去重，再用一次
SQL 查询读取这些不同的 ID。

## 完成、取消与停止

- handler 成功时，同一批的所有请求成功完成。
- handler 抛出异常时，同一批的所有请求都会收到原始异常；日志按批次记录一次失败。
- 批量 `ProcessAsync` 返回的 `Task` 会等待本次提交的所有请求；其中任何一次 handler 调用失败，最终任务都会
  进入失败状态。
- 调用方取消只会移除尚未交给 handler 的请求。批次开始处理后，调用方会收到真实处理结果，避免取消状态掩盖
  已经发生的副作用。
- `StopAsync` 和 `DisposeAsync` 会先停止接收新请求，再排空所有已经接受的请求。取消传给 `StopAsync` 的 token
  只会结束本次等待，后台停止过程仍会继续。

## 依赖注入与日志

`AddRequestBatcher` 会把 handler、协调器和内部 BufferQueue topic 注册到应用现有的 `IServiceCollection`。
应用不需要单独安装或配置 BufferQueue，RequestBatcher 也不会创建或持有另一个 Service Provider。

`Scoped` 和 `Transient` handler 会在每个批次的异步作用域中解析一次。`Singleton` handler 会跨批次复用，
当 `MaxConcurrency > 1` 时必须保证线程安全。

日志使用应用现有的 `Microsoft.Extensions.Logging` 管道，类别为 `RequestBatchCoordinator<TRequest>`。handler
和入队异常会保留原始 exception，但不会记录请求 payload。应用未注册 logger 时使用 `NullLogger`。

## 架构

![RequestBatcher 架构](docs/assets/request-batcher-architecture.png)

调用方只依赖 `IRequestBatcher<TRequest>`。协调器接收单个请求或显式批量提交，把请求交给内部内存分区，按处理
批次调用已注册的 handler，再根据这些结果完成调用方的 `Task`。实线表示请求和批处理流，虚线表示完成结果流。

## 示例与开发

- [PostgreSQL Web API 示例：批量 upsert 与查询去重](samples/RequestBatcher.Deduplication)
- [变更记录](CHANGELOG.md)
- [单元测试](tests/RequestBatcher.Tests)

```bash
dotnet build RequestBatcher.slnx --configuration Release
dotnet test tests/RequestBatcher.Tests/RequestBatcher.Tests.csproj --configuration Release
```

## License

RequestBatcher 使用 [MIT License](LICENSE)。
