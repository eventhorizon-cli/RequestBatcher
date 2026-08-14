# RequestBatcher

**English** | [简体中文](README.zh-CN.md)

RequestBatcher is an in-process .NET SDK that turns concurrent, single-request calls into batched handler invocations.
Callers keep a simple asynchronous API and await their own `Task`; the handler receives an `IReadOnlyList<TRequest>`
that can be written, queried, or sent downstream in one operation.

```text
Caller:  ProcessAsync(TRequest)                 -> Task
Handler: HandleAsync(IReadOnlyList<TRequest>)   -> ValueTask
```

## Features

- Transparent batching: callers submit one request at a time and do not manage batches.
- Opportunistic collection: an available request is not delayed to fill a batch; requests already waiting are handled
  together, up to `BatchSize`.
- Bounded concurrency: `MaxConcurrency` limits the number of handler invocations running at once.
- Partition keys: equal numeric or string keys are routed to the same partition so they do not execute concurrently.
- Per-request completion: every caller observes the success, failure, or cancellation of its own request.
- Backpressure: pending work is bounded, with asynchronous waiting or immediate rejection at capacity.
- Application-owned DI and logging: handler lifetimes are explicit, BufferQueue remains internal, and no nested
  `ServiceProvider` is created.
- Graceful shutdown: accepted requests are drained before the coordinator stops.

## Applicable Scenarios

RequestBatcher is useful when concurrent callers temporarily outpace a downstream dependency and that dependency can
benefit from a batch operation. This is the same producer/consumer speed mismatch that makes an in-process buffer
useful, but RequestBatcher keeps batching behind a single-request API.

- **Database writes:** combine independent writes into batch `INSERT`, `UPDATE`, `UPSERT`, or transaction work to
  reduce round trips and commit overhead.
- **Cache and bulk APIs:** turn many cache updates, cache reads, HTTP/RPC submissions, or event publications into a
  backend operation that already accepts multiple items.
- **Burst smoothing and backpressure:** bound the amount of work retained in memory and limit database or external API
  concurrency with `MaxPendingRequests`, `FullMode`, and `MaxConcurrency`.
- **Per-entity serialization:** use `UsePartitionKey` for orders, products, accounts, inventory, or devices when work
  for one entity must not run concurrently but unrelated entities can run in parallel.
- **Latest-state coalescing:** retain the newest version of a high-frequency update within a batch, then use a version
  check, idempotency key, unique constraint, or transaction in storage to keep cross-batch updates correct.

RequestBatcher is not a durable queue or a replacement for a message broker. It does not persist pending requests,
recover them after a process crash, retry failed handler calls, or provide global ordering with `MaxConcurrency > 1`.
It also returns completion status rather than `Task<TResult>`; a batched read that needs a value should carry a result
holder in the request or update application state from the handler.

Do not submit an operation that must commit or roll back atomically with the caller's transaction. For example, an
order write, inventory decrement, payment charge, and audit record that form one business transaction must remain in
that transaction; sending one step to RequestBatcher breaks the atomic boundary and runs it after the caller's work.
Use RequestBatcher only for independent follow-up work, or first record that work through an explicit transactional
outbox.

## Quick Start

Define the request and a handler that processes one batch:

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

Register the handler and batcher in the same call. The handler lifetime is required:

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

Callers resolve `IRequestBatcher<TRequest>` and submit requests individually:

```csharp
var batcher = serviceProvider.GetRequiredService<IRequestBatcher<OrderWriteRequest>>();

await batcher.ProcessAsync(
    new OrderWriteRequest(OrderId: 42, Amount: 99.50m),
    cancellationToken);
```

The returned `Task` completes only after the request's batch has been processed. Callers do not need to know which
batch or partition handled the request.

A delegate can be registered instead of an `IRequestBatchHandler<TRequest>` implementation:

```csharp
services.AddRequestBatcher<OrderWriteRequest>(
    (batch, cancellationToken) =>
        database.WriteOrdersAsync(batch, cancellationToken),
    ServiceLifetime.Singleton,
    options => options.BatchSize = 256);
```

## Architecture

![RequestBatcher architecture](docs/assets/request-batcher-architecture.png)

The caller only depends on `IRequestBatcher<TRequest>`. The coordinator owns the internal in-memory topic and its
partitions, invokes the registered handler with a batch, then completes each accepted caller `Task` from that batch's
outcome. Solid arrows show request and batch flow; dashed arrows show completion flow.

## How Batches Form

RequestBatcher does not hold the first request for a fixed batching window. When a partition is ready to run, it takes
up to `BatchSize` requests that are already waiting and passes them to the handler. Low traffic can therefore produce
single-request batches, while concurrent traffic naturally produces larger batches.

`BatchSize` is an upper bound, not a minimum. This keeps the SDK useful for latency-sensitive paths without requiring
callers to coordinate submission timing.

## Configuration

| Option | Default | Behavior |
| --- | ---: | --- |
| `BatchSize` | `128` | Maximum number of requests passed to one handler invocation. |
| `MaxConcurrency` | `1` | Maximum concurrent handler invocations and number of processing partitions. |
| `MaxPendingRequests` | `8192` | Maximum accepted requests that have not completed. |
| `FullMode` | `Wait` | Waits asynchronously for capacity. `Fail` throws `RequestBatchQueueFullException` immediately. |
| `UsePartitionKey(...)` | Not set | Uses round-robin routing by default; a selector routes equal keys to one partition. |

With `MaxConcurrency = 1`, processing is globally FIFO. With a higher value, ordering is guaranteed only within a
partition and the handler may be invoked concurrently.

## Partition Keys And Duplicate Request Merging

Use a partition key when requests for the same entity must not be processed at the same time:

```csharp
options.MaxConcurrency = 4;
options.UsePartitionKey(request => request.OrderId);
```

Finite integer-valued numeric keys and non-null string keys are supported. The selector should be deterministic and
side-effect free.

- Equal keys enter the same partition and retain their order after being appended to that partition.
- Concurrent calls do not establish an order before append.
- Different keys may share a partition, so partitioning is not a one-partition-per-key allocation.
- Partitioning does not guarantee that all requests for a key appear in one batch and does not deduplicate data.

`UsePartitionKey` is the routing prerequisite for safe per-entity merging, not the merge operation itself. For example,
when several `PriceUpdate` requests have the same `ProductId`, the handler can group the batch by product and persist
only the highest version. Equal product IDs cannot run concurrently, while unrelated products may still use separate
partitions. If the winning write succeeds, every caller represented by that batch, including callers whose update was
superseded during the merge, completes successfully.

The runnable [duplicate update sample](samples/RequestBatcher.Deduplication) demonstrates both layers required for
correctness: the handler keeps only the highest version for each product within one batch, and the store rejects stale
versions across batches. The storage check remains necessary because a partition key does not make all duplicate
requests arrive in one batch.

## Completion And Failure Semantics

- When the handler succeeds, every request in that batch completes successfully.
- When the handler throws, every request in that batch receives the original handler exception. The failure is logged
  once for the batch.
- Caller cancellation can remove a request only before handler dispatch. After dispatch, the caller observes the real
  batch outcome instead of receiving an ambiguous cancellation.
- `StopAsync` and `DisposeAsync` stop accepting new requests and drain all requests that were already accepted.

RequestBatcher is an in-process scheduling component. It does not persist pending requests or recover them after a
process failure. If requests must survive a crash, place the durability boundary in the handler with a database
transaction, WAL, or reliable messaging system.

## Dependency Injection And Logging

`AddRequestBatcher` registers the handler and encapsulated BufferQueue topic in the application's existing
`IServiceCollection`. Callers do not install or configure BufferQueue separately, and RequestBatcher does not build its
own service provider.

Scoped and transient handlers are resolved once per batch in an async scope. A singleton handler is reused across
batches and must be thread-safe when `MaxConcurrency > 1`.

Logging uses the application's `Microsoft.Extensions.Logging` pipeline with the category
`RequestBatchCoordinator<TRequest>`. Handler and enqueue failures include their original exception. Request payloads
are never written to logs. If no logger is registered, RequestBatcher uses `NullLogger`.

## Repository

- [Duplicate update sample](samples/RequestBatcher.Deduplication)
- [Unit tests](tests/RequestBatcher.Tests)
- [PostgreSQL and Redis benchmarks](tests/RequestBatcher.Benchmarks)

```bash
dotnet build RequestBatcher.slnx --configuration Release
dotnet test tests/RequestBatcher.Tests/RequestBatcher.Tests.csproj --configuration Release
```

## License

RequestBatcher is available under the [MIT License](LICENSE).
