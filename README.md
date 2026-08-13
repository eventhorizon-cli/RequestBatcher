# RequestBatcher

**English** | [简体中文](README.zh-CN.md)

RequestBatcher opportunistically coalesces individually submitted requests into batches. Each caller only awaits its own
`Task`; queueing, partitioning, and batch scheduling remain transparent.

The design follows goleveldb's write-merge approach: the first runnable request does not wait for a fixed batching
window. Instead, the coordinator combines requests that have already arrived before dispatch. BufferQueue memory pull
consumers provide batched delivery, while the partition count bounds concurrent batch execution.

## Usage

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

A handler delegate can be registered in the same call:

```csharp
services.AddRequestBatcher<SaveOrder>(
    async (batch, cancellationToken) =>
        await database.SaveOrdersAsync(batch, cancellationToken),
    ServiceLifetime.Singleton,
    options => options.BatchSize = 256);
```

`AddRequestBatcher` extends `IServiceCollection` and encapsulates the internal BufferQueue memory topic. Callers do not
need to install, register, or configure BufferQueue. BufferQueue, the handler, and the coordinator are all created and
disposed by the application's existing DI container; RequestBatcher never builds a nested `ServiceProvider`.

The handler lifetime is explicit. Scoped and transient handlers are resolved once per batch and their async scope is
disposed after the batch completes. A singleton handler is reused across batches and must be thread-safe when
`MaxConcurrency > 1`.

## Processing Semantics

- `BatchSize` is the maximum number of requests in a batch, not a target that must be filled. There is no fixed delay.
- When the handler succeeds, every request in that batch succeeds. If it throws, every request receives the same exception.
- `MaxConcurrency = 1` preserves global FIFO. Higher values use multiple partitions and guarantee ordering only within each partition; routing is round-robin unless `UsePartitionKey` is configured.
- `UsePartitionKey` routes equal numeric or string keys to the same partition. This preserves their order after they are appended to that partition while allowing different keys to be processed concurrently.
- Caller cancellation applies only before handler dispatch. Once dispatched, the call observes the actual batch outcome, preventing retries from unknowingly duplicating side effects.
- At `MaxPendingRequests`, the default behavior asynchronously waits for capacity. Set `FullMode = RequestBatchFullMode.Fail` to immediately throw `RequestBatchQueueFullException`.
- `StopAsync` and `DisposeAsync` stop admission first and then drain every accepted request.

RequestBatcher is an in-process scheduling component. It does not persist pending requests or recover them after process
failure. When requests must not be lost, establish the durability boundary in the handler with a database transaction,
WAL, or reliable messaging system.

## Logging

RequestBatcher uses the application's existing `Microsoft.Extensions.Logging` pipeline with the category
`RequestBatchCoordinator<TRequest>`. ASP.NET Core applications normally require no additional setup; other applications
can call `services.AddLogging(...)` on their external container. When no logger is registered, RequestBatcher uses
`NullLogger` and continues without creating an internal `ServiceProvider`.

- `Trace`: batch start and successful completion, including batch size.
- `Debug`: coordinator startup and shutdown.
- `Warning`: request rejection when `FullMode = Fail` reaches capacity.
- `Error`: enqueue failure or handler failure; the original exception is attached.
- `Critical`: unexpected BufferQueue consumer termination.

Logs contain stable metadata such as request type, batch size, and capacity, but never request payloads. A handler
failure is logged once per batch, while every caller still receives the original exception through its own `Task`.

## Partition Keys And Duplicate Merging

Configure a partition key when requests for the same entity must never be processed concurrently:

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

Finite integer-valued numeric keys and non-null string keys are supported. The selector must be deterministic and
side-effect free. Equal keys are routed to the same partition and retain their order after they are appended to that
partition; concurrent calls do not establish an order before append. Different keys can share a partition, and key
routing does not guarantee that all updates for an entity appear in one batch.

The runnable [`samples/RequestBatcher.Deduplication`](samples/RequestBatcher.Deduplication) example merges repeated
price updates in two layers: the handler retains only the highest version for each product within a batch, and the
store applies updates conditionally by version across batches. The second layer is required because batching is
opportunistic rather than a global deduplication boundary.

```bash
dotnet run --project samples/RequestBatcher.Deduplication/RequestBatcher.Deduplication.csproj
```

## PostgreSQL Benchmark

`tests/RequestBatcher.Benchmarks` uses BenchmarkDotNet and Testcontainers to start an isolated PostgreSQL instance and
compare two paths for the same logical writes:

- `DirectSingleWritesAsync`: one independently committed single-row `INSERT` per request.
- `RequestBatcherWritesAsync`: callers still invoke `ProcessAsync` individually, while the handler uses PostgreSQL
  `unnest` to issue one multi-row `INSERT` per aggregated batch.

Both paths process the same 1,000 requests with the same schema, connection pool, and maximum database concurrency.
Container startup, schema creation, pool warmup, and per-iteration `TRUNCATE` are outside measured regions. Cleanup
validates the persisted row count and confirms that the SDK path actually formed batches. Defaults are
`BatchSize = 100` and `MaxConcurrency = 4`; Docker is required and the image is pinned to
`postgres:17.6-alpine`.

```bash
dotnet run --project tests/RequestBatcher.Benchmarks/RequestBatcher.Benchmarks.csproj \
  --configuration Release -- \
  --filter '*PostgreSqlWriteBenchmarks*'
```

Each reported operation processes all 1,000 logical requests, not one request. This benchmark primarily measures the
benefit of fewer database round trips and commits; it is not a portable database-capacity measurement.

## Redis Benchmarks

`tests/RequestBatcher.Benchmarks/Redis` uses Testcontainers to start a pinned single-node Redis instance and defines
separate read and write benchmark classes:

- `RedisReadBenchmarks`: compares one `GET` per request with one `MGET` per batch.
- `RedisWriteBenchmarks`: compares one `SET` per request with one `MSET` per batch.

Both paths reuse the same StackExchange.Redis `ConnectionMultiplexer` and concurrently submit the same 1,000 logical
requests. The direct path may therefore be automatically pipelined by the client, but still sends 1,000 distinct Redis
commands. Container startup, connection warmup, read-data seeding, write-data reset, and result validation are outside
measured regions. Every read result and persisted write value is verified, and the SDK path additionally verifies that
all requests were handled and that batching occurred. Defaults are `BatchSize = 100`, `MaxConcurrency = 4`, and the
image is pinned to `redis:7.4.5-alpine`.

Set `REQUEST_BATCHER_REDIS_IMAGE` to use a compatible mirror or locally cached image when the default Docker Hub image
is unavailable. Leaving it unset keeps the pinned default.

```bash
dotnet run --project tests/RequestBatcher.Benchmarks/RequestBatcher.Benchmarks.csproj \
  --configuration Release -- \
  --filter '*RedisReadBenchmarks*'

dotnet run --project tests/RequestBatcher.Benchmarks/RequestBatcher.Benchmarks.csproj \
  --configuration Release -- \
  --filter '*RedisWriteBenchmarks*'
```

Each Redis operation also represents 1,000 logical requests. In a Redis Cluster, every key in an `MGET` or `MSET`
must share a hash slot; the benchmark uses a single-node Redis instance, so that restriction does not apply here.
