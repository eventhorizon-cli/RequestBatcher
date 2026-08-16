# RequestBatcher

[![NuGet](https://img.shields.io/nuget/v/RequestBatcher.svg)](https://www.nuget.org/packages/RequestBatcher)
[![Build](https://github.com/eventhorizon-cli/RequestBatcher/actions/workflows/dotnet-build.yml/badge.svg)](https://github.com/eventhorizon-cli/RequestBatcher/actions/workflows/dotnet-build.yml)
[![Codecov](https://codecov.io/gh/eventhorizon-cli/RequestBatcher/graph/badge.svg)](https://codecov.io/gh/eventhorizon-cli/RequestBatcher)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

[English](README.md) | **简体中文**

RequestBatcher 在 .NET 进程内收集分散提交的并发请求，合并成批次后交给处理器统一处理。调用方可以继续
逐条提交请求并等待一个 `Task`，不需要知道请求进入了哪个批次。

## 适用场景

请求彼此独立、允许在内存中短暂排队，并且下游一次处理多项更合适时，可以使用 RequestBatcher：

- 可以使用批量 `INSERT`、`UPDATE` 或 `UPSERT` 的数据库写入；
- 原生支持多项操作的缓存读写或下游接口；
- 需要限制排队量和下游并发量的短时流量突发；
- 调用方取消时只需撤销尚未分发的请求；已经分发的操作应继续执行，并返回实际处理结果；
- 需要分区内顺序，或希望在单批内合并相关请求。

## 不适用场景

RequestBatcher 不是持久化后台队列，也不是事务协调器：

- 已接收的工作必须在进程故障后恢复。此时应使用持久化存储或可靠消息队列。
- 操作必须与调用方事务一起提交或回滚。它应留在原事务内；独立的后续工作可以通过 Transactional Outbox
  记录后再处理。
- 每次调用都需要直接返回 `TResult`。RequestBatcher 只通过 `Task` 返回完成状态；批量查询需要让请求携带
  自己的结果容器，或更新应用状态。
- 下游操作依赖自动重试或恰好一次（exactly-once）副作用。RequestBatcher 不提供这些保证。
- 处理器要求每批达到最低请求数或使用固定的收集时间。RequestBatcher 会直接处理当前已经排队的请求。
- 调用方断开、超时或取消后，正在执行的下游操作也必须立即停止。一个处理批次可能包含多个调用方的请求，
  因此单个调用方的 `CancellationToken` 不会传给处理器，也不能取消这个共享的处理器调用。

## 工作方式

1. 调用方通过 `ProcessAsync` 提交一个请求。
2. RequestBatcher 根据容量配置接收请求，并将它路由到一个内存分区。
3. 分区可以开始处理时，RequestBatcher 从当前已经排队的请求中取出最多 `BatchSize` 个，只调用一次处理器。
4. 处理器的结果会完成该批次中每个调用方拿到的 `Task`。

`BatchSize` 只限制每批最多包含多少请求，不要求达到这个数量才开始处理。低流量时每批可能只有一个请求；
并发请求增多时，同一批中通常会包含更多请求。

![RequestBatcher 架构](docs/assets/request-batcher-architecture.png)

调用方和处理器使用的是两层不同的 API：

| 使用方 | API | 含义 |
| --- | --- | --- |
| 调用方 | `ProcessAsync(TRequest)` | 提交一个请求，返回反映该请求真实处理结果的 `Task`。 |
| 调用方 | `ProcessAsync(IEnumerable<TRequest>)` | 提交已有的一组请求，返回等待整次提交完成的一个 `Task`。 |
| 处理器 | `HandleAsync(IReadOnlyList<TRequest>)` | 处理 RequestBatcher 选出的一个批次，以 `ValueTask` 表示完成。 |

处理器返回的 `ValueTask` 只会在 RequestBatcher 内部等待一次，不会返回给调用方。它允许同步完成的处理器避免
分配 `Task`；普通异步 I/O 仍然可以直接用 `async ValueTask` 实现。

## 安装

```bash
dotnet add package RequestBatcher
```

## 快速开始

定义请求，以及处理一个批次的代码：

```csharp
public sealed record OrderWriteRequest(long OrderId, decimal Amount);

public interface IOrderStore
{
    Task WriteBatchAsync(
        IReadOnlyList<OrderWriteRequest> requests,
        CancellationToken cancellationToken);
}

public sealed class OrderWriteBatchHandler(IOrderStore store)
    : IRequestBatchHandler<OrderWriteRequest>
{
    public async ValueTask HandleAsync(
        IReadOnlyList<OrderWriteRequest> requests,
        CancellationToken cancellationToken = default)
    {
        await store.WriteBatchAsync(requests, cancellationToken);
    }
}
```

在一次调用中注册处理器和 RequestBatcher。处理器生命周期也是注册参数；最小配置只需指定批次上限：

```csharp
services.AddRequestBatcher<OrderWriteRequest, OrderWriteBatchHandler>(
    ServiceLifetime.Scoped,
    options =>
    {
        options.BatchSize = 256;
    });
```

在正常的应用调用链中注入 `IRequestBatcher<TRequest>`：

```csharp
public sealed class OrderService(IRequestBatcher<OrderWriteRequest> batcher)
{
    public Task SaveAsync(
        OrderWriteRequest request,
        CancellationToken cancellationToken = default) =>
        batcher.ProcessAsync(request, cancellationToken);
}
```

`SaveAsync` 直接返回 RequestBatcher 创建的 `Task`。处理器完成该请求后，这个 `Task` 才会完成；调用方
不需要知道请求进入了哪个批次或分区。不想单独定义处理器类型时，也可以注册处理器委托。

### 提交已有的请求组

调用方已经持有多个请求时，可以一次提交：

```csharp
await batcher.ProcessAsync(orderWriteRequests, cancellationToken);
```

RequestBatcher 会先取得请求序列的快照，再把它作为一个整体申请容量。返回的 `Task` 会等待这次提交中的所有
请求。一次提交不等于一次处理器调用：请求仍可能按 `BatchSize` 或分区路由拆成多个处理批次。

> **显式批量提交不是分区边界。** 配置多个分区后，RequestBatcher 会逐项路由，而不会把整次提交放进一个分区。
> 只有 `MaxConcurrency = 1`，或者每项计算出相同的分区键时，才保证整组请求落在同一个分区。

## 单次提交与批量提交

本文中的**提交**指调用方执行一次 `ProcessAsync`；**处理批次**指一次 `HandleAsync` 收到的
`IReadOnlyList<TRequest>`。两者不是同一个边界。

| 行为 | 单次提交 | 显式批量提交 |
| --- | --- | --- |
| 输入 | 直接使用传入的 `TRequest`。 | 对序列枚举一次并取得快照；空序列立即完成。 |
| 容量 | 为一个请求申请容量。 | 为整组请求原子申请容量；数量超过 `MaxPendingRequests` 时拒绝整组。 |
| 路由 | 按当前路由模式处理这个请求。 | 每个请求独立路由，因此一次提交可以跨多个分区。 |
| 处理器调用 | 可能与同一分区中已经排队的其他请求一起交给处理器。 | 不创建处理批次边界；请求可以按分区和 `BatchSize` 拆分，并可能并行执行。 |
| 完成 | 返回的 `Task` 表示这个请求的真实处理结果。 | 返回的 `Task` 等待本次提交中的每个请求。 |
| 失败 | 一次处理器调用失败时，该处理批次中的所有请求都会失败。 | 部分请求可能已经成功，另一个处理批次随后失败；整组 `Task` 会失败，但不会回滚已经成功的操作。 |
| 取消 | 只能取消尚未交给处理器的请求。 | 同一个 token 用于整组中的每个请求；尚未分发的请求可以取消，已经分发的请求继续返回真实结果。 |

## 路由模式

`MaxConcurrency` 同时决定处理器最大并发调用数和处理分区数。每次处理器调用只接收一个分区中的请求。

| 配置 | 请求如何路由 | 对显式批量提交的影响 | 顺序保证 |
| --- | --- | --- | --- |
| `MaxConcurrency = 1` | 所有请求进入唯一分区。 | 请求留在同一分区，但仍可能按 `BatchSize` 拆分。 | 全局写入顺序。 |
| `MaxConcurrency > 1`，未配置分区键 | 每个请求依次轮询到下一个分区。 | 请求逐项分散到不同分区，处理器调用可以并行执行。 | 只保证分区内顺序。 |
| `MaxConcurrency > 1`，配置 `UsePartitionKey` | 每个请求都会执行选择器；相同 key 进入同一分区。 | 按选择结果拆到不同分区；相同 key 保留其在输入中的顺序。 | 分区内有序；不同 key 可能共用一个分区。 |

顺序从请求写入分区后开始计算。多个调用方并发提交时，在写入分区之前没有额外的先后保证。
`MaxConcurrency = 1` 时只有一个分区，此时配置分区键不会改变路由结果。

## 处理语义

### 成功与失败

- 处理器正常完成时，该处理批次中的所有调用方都会成功完成。
- 处理器抛出异常时，这些调用方都会收到原始异常。
- RequestBatcher 不会自动重试失败的处理器。
- 批量重载会等待本次提交中的所有请求；只要涉及的某次处理器调用失败，最终 `Task` 就会失败。
- 一次显式批量提交跨越多个处理器调用，且其中多次调用都失败时，`await` 会按普通 `Task` 语义抛出其中一个
  原始异常；所有不同的异常实例都可以从 `Task.Exception.InnerExceptions` 读取。同一次处理器异常即使影响
  多个请求，也只记录一次。

### 调用方取消

调用方取消只能移除尚未交给处理器的请求。处理器开始执行后，RequestBatcher 会返回真实处理结果，而不会把
调用方的 `Task` 改成已取消。这样可以避免取消状态掩盖已经发生的副作用。

传给处理器的 token 属于 RequestBatcher 自身的处理生命周期，不属于某一个调用方。因此，一个调用方取消
不会中止同一批中的其他请求。

显式批量提交会把同一个调用方 token 应用到每个请求，因此取消后可能同时存在“尚未分发而取消”的请求和
“已经分发并继续执行”的请求。返回的 `Task` 仍会等待全部请求；其中存在失败时最终状态为失败，否则只要有
请求被取消，最终状态就是取消。已经完成的副作用不会回滚。

### 容量与背压

`MaxPendingRequests` 限制已接收但尚未完成的请求数量：

- `FullMode = Wait` 会异步等待足够容量，并且在等待期间响应调用方取消。
- `FullMode = Fail` 会立即返回以 `RequestBatchQueueFullException` 失败的 `Task`。
- 显式提交的一组请求会原子申请容量：要么整组接收，要么整组不接收。
- 请求组数量超过 `MaxPendingRequests` 时始终会被拒绝。

### 停止

`StopAsync` 和 `DisposeAsync` 会停止接收新请求，并排空已经接收的请求。取消传给 `StopAsync` 的 token
只会结束本次等待，后台停止过程仍会继续。

## 配置

| 选项 | 默认值 | 行为 |
| --- | ---: | --- |
| `BatchSize` | `128` | 单次处理器调用包含的请求数量上限。 |
| `MaxConcurrency` | `1` | 处理器最大并发调用数，同时也是处理分区数。 |
| `MaxPendingRequests` | `8192` | 已接收但尚未完成的请求上限，也是一次显式提交的数量上限。 |
| `FullMode` | `Wait` | 默认等待容量；`Fail` 在容量不足时立即拒绝。 |
| `UsePartitionKey(...)` | 未配置 | 默认轮询分配；选择结果相同的 key 会进入同一分区。 |

`MaxConcurrency = 1` 时保持全局 FIFO。提高该值后，处理器可以并行执行，只保证每个分区内部的顺序。

## 分区键

分区键（Partition Key）是可选的相关请求路由规则：

```csharp
options.MaxConcurrency = 4;
options.UsePartitionKey(request => request.OrderId);
```

相同的有限、整数值数值键，或相同的非 `null` 字符串键，会进入同一分区，并按进入分区的顺序处理。不同 key
仍可能落入同一分区，因此 key 与分区不是一一对应关系；并发调用在进入分区之前也没有额外的先后保证。

分区路由不会强制同一 key 的全部请求进入同一个处理批次，也不会自动去重。它提供一个有序处理边界，处理器
可以在此基础上实现重复更新合并等业务逻辑。

### 示例：合并重复更新

假设多个 `PriceUpdate` 具有相同的 `ProductId`。处理器可以在当前批次中按商品分组，只写入版本最高的更新。
按 `ProductId` 路由可以避免两个处理器调用并发处理同一个商品，其他商品仍可以并行处理。

分区键不能让同一商品的所有更新都进入同一批，因此存储层仍需防止旧版本跨批次覆盖新状态。可运行的
[PostgreSQL Web API 示例](samples/RequestBatcher.Deduplication)展示了完整做法：

- 写处理器在单批内合并同一商品的更新，再执行一次批量 upsert；
- upsert 条件会忽略后续批次中的旧版本；
- 查询处理器先对商品 ID 去重，再为当前批次执行一次 SQL 查询。

## 依赖注入与日志

`AddRequestBatcher` 会把处理器、协调器和内部 BufferQueue topic 注册到应用已有的 `IServiceCollection`。
应用不直接配置 BufferQueue，RequestBatcher 也不会创建嵌套的 Service Provider。

`Scoped` 和 `Transient` 处理器会在每个处理批次的异步作用域中解析一次。`Singleton` 处理器会跨批次
复用；`MaxConcurrency > 1` 时，它必须保证线程安全。

日志使用应用现有的 `Microsoft.Extensions.Logging` 管道，类别为
`RequestBatchCoordinator<TRequest>`。处理器异常会连同原始 exception 一起记录，请求内容不会写入日志。

## 示例与开发

- [PostgreSQL Web API 示例](samples/RequestBatcher.Deduplication)
- [变更记录](CHANGELOG.md)
- [单元测试](tests/RequestBatcher.Tests)

```bash
dotnet build RequestBatcher.slnx --configuration Release
dotnet test tests/RequestBatcher.Tests/RequestBatcher.Tests.csproj --configuration Release
```

## License

RequestBatcher 使用 [MIT License](LICENSE)。
