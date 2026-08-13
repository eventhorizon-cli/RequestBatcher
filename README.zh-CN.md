# RequestBatcher

[English](README.md) | **简体中文**

RequestBatcher 把调用方提交的单个请求机会式地合并成批次。调用方只需要等待自己的 `Task`，不需要感知
队列、分区或批处理调度。

它参考 goleveldb 的 write merge 思路：第一个可执行请求不会等待固定窗口，而是在处理前合并
此刻已经到达的并发请求。底层使用 BufferQueue 的 Memory pull consumer 做批量提取，并通过
partition 数量限制并行处理批次的数量。

## 基本使用

```csharp
public sealed record SaveOrder(long Id, decimal Amount);

public interface IOrderStore
{
    ValueTask SaveAsync(
        IReadOnlyList<SaveOrder> orders,
        CancellationToken cancellationToken);
}

public sealed class SaveOrderHandler(IOrderStore store) : IRequestBatchHandler<SaveOrder>
{
    public async ValueTask HandleAsync(
        IReadOnlyList<SaveOrder> requests,
        CancellationToken cancellationToken = default)
    {
        await store.SaveAsync(requests, cancellationToken);
    }
}

services.AddRequestBatcher<SaveOrder, SaveOrderHandler>(ServiceLifetime.Singleton, options =>
{
    options.BatchSize = 256;
    options.MaxConcurrency = 4;
    options.MaxPendingRequests = 10_000;
    options.UsePartitionKey(order => order.Id);
});

var batcher = serviceProvider.GetRequiredService<IRequestBatcher<SaveOrder>>();
await batcher.ProcessAsync(new SaveOrder(42, 99.50m), cancellationToken);
```

也可以把 handler 委托和注册放在同一次调用中：

```csharp
services.AddRequestBatcher<SaveOrder>(
    async (batch, cancellationToken) =>
        await database.SaveOrdersAsync(batch, cancellationToken),
    ServiceLifetime.Singleton,
    options => options.BatchSize = 256);
```

`AddRequestBatcher` 是 `IServiceCollection` 扩展。它会封装并注册内部使用的 BufferQueue Memory topic，调用方
不需要安装、注册或配置 BufferQueue。BufferQueue、handler 和协调器仍全部由应用自己的 DI 容器创建和
释放；RequestBatcher 不会在内部构建嵌套的 `ServiceProvider`。handler 生命周期通过
`ServiceLifetime` 参数明确指定。`Scoped` 和 `Transient` handler 每个批次解析一次，并在批次处理结束后
释放对应作用域；`Singleton` handler 会跨批次复用，当 `MaxConcurrency > 1` 时必须是线程安全的。

## 处理语义

- `BatchSize` 是单批上限，不是必须凑满的数量；没有固定攒批延迟。
- handler 成功时，同一批的所有调用成功；handler 抛出异常时，同一批的所有调用收到同一个异常。
- `MaxConcurrency = 1` 时保持全局 FIFO。大于一时会使用多个 partition，只保证各 partition 内有序；未配置 `UsePartitionKey` 时采用轮询分配，handler 必须支持并发调用。
- `UsePartitionKey` 可将相同数值或字符串 key 的请求路由到同一 partition，在允许不同 key 并行处理的同时保持写入该 partition 后的顺序。
- 取消只在请求尚未交给 handler 时生效。一旦批次开始处理，调用会等待该批次的真实结果，避免取消后重试造成重复写入。
- 默认在待处理请求达到 `MaxPendingRequests` 后异步等待容量。设置 `FullMode = RequestBatchFullMode.Fail` 可改为快速抛出 `RequestBatchQueueFullException`。
- `StopAsync` 和 `DisposeAsync` 会先停止接收新请求，再排空所有已经接受的请求。

RequestBatcher 是进程内调度组件，不提供持久化或进程崩溃后的请求恢复。如果请求不能丢失，应在 handler
中使用数据库事务、WAL 或可靠消息系统承担持久化边界。

## 日志

RequestBatcher 使用应用现有的 `Microsoft.Extensions.Logging` 管道，日志类别为
`RequestBatchCoordinator<TRequest>`。ASP.NET Core 应用通常不需要额外配置；其他应用可在外部容器调用
`services.AddLogging(...)`。没有注册 logger 时会自动使用 `NullLogger`，不会影响 SDK 注册或执行，也
不会创建内部 `ServiceProvider`。

- `Trace`：批次开始和成功，包含批次大小。
- `Debug`：协调器启动和停止。
- `Warning`：`FullMode = Fail` 时因队列达到容量而拒绝请求。
- `Error`：请求入队失败或 handler 处理批次失败；handler 的原始异常会附加到日志。
- `Critical`：BufferQueue consumer 意外退出。

日志只包含请求类型、批次大小和容量等元数据，不会记录请求对象本身。handler 失败时每个调用方仍会
通过自己的 `Task` 收到原始异常，同一批只记录一条错误日志。

## Partition Key 与重复数据合并

同一实体的请求不能并行处理时，可以配置 partition key：

```csharp
services.AddRequestBatcher<PriceUpdate, PriceUpdateHandler>(
    ServiceLifetime.Singleton,
    options =>
    {
        options.BatchSize = 100;
        options.MaxConcurrency = 4;
        options.UsePartitionKey(update => update.ProductId);
    });
```

目前支持值为有限整数的数值 key 和非 `null` 字符串 key；selector 必须是确定且无副作用的函数。相同
key 会进入同一 partition，并保持写入该 partition 后的顺序；并发调用在写入前没有额外的先后保证。
不同 key 可能共享 partition，而且 key 路由不保证同一实体的全部更新一定落入同一个批次。

可运行的 [`samples/RequestBatcher.Deduplication`](samples/RequestBatcher.Deduplication) 示例使用两层策略合并
重复价格更新：handler 在单批内按产品只保留最高版本，存储层再通过版本条件处理跨批次更新。第二层不能
省略，因为攒批是机会式行为，并不是全局去重边界。

```bash
dotnet run --project samples/RequestBatcher.Deduplication/RequestBatcher.Deduplication.csproj
```

## PostgreSQL 性能基准

`tests/RequestBatcher.Benchmarks` 使用 BenchmarkDotNet 和 Testcontainers 启动独立 PostgreSQL，比较同一批
逻辑请求的两种写入方式：

- `DirectSingleWritesAsync`：每个请求执行一次独立、自动提交的单行 `INSERT`。
- `RequestBatcherWritesAsync`：调用方仍逐个调用 `ProcessAsync`，handler 通过 PostgreSQL `unnest` 为每个
  聚合批次执行一次多行 `INSERT`。

两条路径使用相同的 1,000 个请求、表结构、连接池及最大数据库并发度。容器启动、建表、连接池预热和
每轮 `TRUNCATE` 均不计时；每轮结束后还会校验数据库行数，并确认 SDK 路径确实形成了批次。默认参数为
`BatchSize = 100`、`MaxConcurrency = 4`。运行前需要可用的 Docker daemon；首次运行会拉取固定的
`postgres:17.6-alpine` 镜像。

```bash
dotnet run --project tests/RequestBatcher.Benchmarks/RequestBatcher.Benchmarks.csproj \
  --configuration Release -- \
  --filter '*PostgreSqlWriteBenchmarks*'
```

结果中的每次 operation 代表完整处理 1,000 个逻辑请求，而不是单个请求。这个基准主要反映减少数据库
往返和事务提交次数的收益；它不是跨机器可直接比较的数据库容量指标。

## Redis 性能基准

`tests/RequestBatcher.Benchmarks/Redis` 使用 Testcontainers 启动固定版本的单节点 Redis，并分别提供查询和
写入两组基准：

- `RedisReadBenchmarks`：比较每个请求一条 `GET` 与每批一条 `MGET`。
- `RedisWriteBenchmarks`：比较每个请求一条 `SET` 与每批一条 `MSET`。

两条路径都复用同一个 `StackExchange.Redis` `ConnectionMultiplexer`，并发提交相同的 1,000 个逻辑请求；
因此直接路径可能由客户端自动 pipeline，但仍会发送 1,000 条独立命令。容器启动、连接预热、读数据预置、
写数据清理和结果校验均不计时。每轮查询会核对全部返回值，每轮写入会使用 `MGET` 核对全部持久化值；SDK
路径还会确认 handler 处理了所有请求且确实形成了批次。默认参数同样是 `BatchSize = 100`、
`MaxConcurrency = 4`，容器镜像固定为 `redis:7.4.5-alpine`。

默认 Docker Hub 镜像不可用时，可通过 `REQUEST_BATCHER_REDIS_IMAGE` 指定兼容的镜像代理或本地缓存镜像；
不设置时仍使用上述固定版本。

```bash
dotnet run --project tests/RequestBatcher.Benchmarks/RequestBatcher.Benchmarks.csproj \
  --configuration Release -- \
  --filter '*RedisReadBenchmarks*'

dotnet run --project tests/RequestBatcher.Benchmarks/RequestBatcher.Benchmarks.csproj \
  --configuration Release -- \
  --filter '*RedisWriteBenchmarks*'
```

Redis 基准中的每次 operation 同样代表完整处理 1,000 个逻辑请求。`MGET`/`MSET` 在 Redis Cluster 中要求
同批 key 位于相同 hash slot；当前 Testcontainers 基准使用单节点 Redis，不受该限制。
