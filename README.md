# RequestBatcher

[![NuGet](https://img.shields.io/nuget/v/RequestBatcher.svg)](https://www.nuget.org/packages/RequestBatcher)
[![Build](https://github.com/eventhorizon-cli/RequestBatcher/actions/workflows/dotnet-build.yml/badge.svg)](https://github.com/eventhorizon-cli/RequestBatcher/actions/workflows/dotnet-build.yml)
[![Codecov](https://codecov.io/gh/eventhorizon-cli/RequestBatcher/graph/badge.svg)](https://codecov.io/gh/eventhorizon-cli/RequestBatcher)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

**English** | [简体中文](README.zh-CN.md)

RequestBatcher batches concurrent, in-process .NET requests without exposing batch coordination to callers. A caller
submits one `TRequest` or an existing sequence and awaits a `Task`; the handler receives an `IReadOnlyList<TRequest>`
and can write, query, or call a downstream service once for the batch.

```text
Caller:  ProcessAsync(TRequest)                       -> Task
         ProcessAsync(IEnumerable<TRequest>)          -> Task
Handler: HandleAsync(IReadOnlyList<TRequest>)         -> ValueTask
```

## Features

- **Transparent batching:** callers submit requests individually and never create or manage batches.
- **Explicit batch submission:** callers that already have multiple requests can enqueue them with one production
  operation and await one completion task.
- **Opportunistic collection:** each batch takes up to `BatchSize` requests that are already queued, without holding the
  first request for a fixed batching window.
- **Bounded concurrency:** `MaxConcurrency` limits how many handler calls may run at once.
- **Partition routing:** equal numeric or string keys are routed to the same processing partition.
- **Per-request completion:** every caller observes the success, failure, or cancellation of its own request.
- **Backpressure:** pending work is bounded, with asynchronous waiting or immediate rejection when capacity is full.
- **Application-owned infrastructure:** RequestBatcher uses the application's DI and logging setup, keeps BufferQueue
  internal, and never creates a nested `ServiceProvider`.
- **Graceful shutdown:** accepted requests are drained before processing stops.

## Installation

```bash
dotnet add package RequestBatcher
```

## Quick Start

Define a request and a handler that processes one batch:

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

Register the handler and batcher together. The handler lifetime is explicit:

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

Resolve `IRequestBatcher<TRequest>` and submit requests one at a time:

```csharp
var batcher = serviceProvider.GetRequiredService<IRequestBatcher<OrderWriteRequest>>();

await batcher.ProcessAsync(
    new OrderWriteRequest(OrderId: 42, Amount: 99.50m),
    cancellationToken);
```

The returned `Task` completes after the request's batch has been handled. The caller does not need to know which batch
or partition received the request.

When requests are already available as a group, submit them together:

```csharp
await batcher.ProcessAsync(orderWriteRequests, cancellationToken);
```

The batch overload of `ProcessAsync` snapshots the sequence and admits it as one unit. Its `Task` completes after every
submitted request has finished. Requests may still be split across partitions or handler calls, so this API does not
override `BatchSize` or create a handler-batch boundary.

A delegate can be registered instead of an `IRequestBatchHandler<TRequest>` implementation:

```csharp
services.AddRequestBatcher<OrderWriteRequest>(
    (batch, cancellationToken) =>
        database.WriteOrdersAsync(batch, cancellationToken),
    ServiceLifetime.Singleton,
    options => options.BatchSize = 256);
```

## When to Use It

RequestBatcher fits an in-process call path where concurrent callers can temporarily outpace a downstream dependency
and that dependency already benefits from handling multiple items at once.

- **Database writes:** combine independent operations into batch `INSERT`, `UPDATE`, `UPSERT`, or transaction work to
  reduce round trips and commit overhead.
- **Cache and bulk APIs:** combine cache reads or writes, HTTP/RPC submissions, or event publications when the downstream
  API accepts multiple items.
- **Burst smoothing:** bound queued work and downstream concurrency with `MaxPendingRequests`, `FullMode`, and
  `MaxConcurrency`.
- **Keyed workloads:** route requests by order, product, account, inventory item, or device so equal keys share a
  partition while other keys can run in parallel.
- **Repeated state updates:** merge updates within a batch, then use version checks, idempotency keys, unique constraints,
  or transactions to preserve correctness across batches.

## When Not to Use It

- **Durable work:** pending requests live only in memory and cannot be recovered after a process failure. Use a database,
  WAL, or reliable message broker when accepted work must survive a crash.
- **Work inside the caller's transaction:** an operation that must commit or roll back atomically with the caller must
  stay in that transaction. Record independent follow-up work with a transactional outbox before batching it.
- **Independent return values:** the public API returns completion through `Task`, not `Task<TResult>`. A batched read must
  carry its own result holder or have the handler update application state.
- **Global ordering with parallel handlers:** global FIFO requires `MaxConcurrency = 1`; higher values preserve order only
  within each partition.
- **Automatic retries or exactly-once effects:** handler failures are returned to callers but are not retried. Retried
  operations need idempotency or another application-level safeguard.

## How Batches Form

RequestBatcher uses opportunistic batching. When a partition is ready, it takes up to `BatchSize` requests that are
already queued and passes them to the handler. Low traffic may produce single-request batches; concurrent traffic tends
to produce larger batches.

`BatchSize` is an upper bound, not a minimum. RequestBatcher does not add a fixed delay to wait for a full batch, and
callers do not need to coordinate submission timing.

The batch overload uses one BufferQueue batch-production operation, but routing still happens per request. A submitted
group can therefore be split by partition and by `BatchSize` before it reaches the handler.

## Configuration

| Option | Default | Behavior |
| --- | ---: | --- |
| `BatchSize` | `128` | Maximum requests passed to one handler call. |
| `MaxConcurrency` | `1` | Maximum concurrent handler calls and number of processing partitions. |
| `MaxPendingRequests` | `8192` | Maximum accepted requests that have not completed, and maximum size of one explicit submission. |
| `FullMode` | `Wait` | Waits asynchronously for capacity. `Fail` returns a `Task` faulted with `RequestBatchQueueFullException`. |
| `UsePartitionKey(...)` | Not set | Uses round-robin routing by default; a selector routes equal keys to one partition. |

With `MaxConcurrency = 1`, requests are processed in global FIFO order. With a higher value, handler calls may run in
parallel and ordering is guaranteed only within each partition.

Capacity is reserved for an explicit batch as one unit. In `Wait` mode, `ProcessAsync` waits until the whole batch
fits. In `Fail` mode, it rejects the whole batch without accepting a prefix. A batch larger than
`MaxPendingRequests` is always rejected with `RequestBatchQueueFullException`; `RequestedCount` reports its size.

## Partition Keys

`UsePartitionKey` determines which processing partition receives a request. The selector reads a stable business key;
requests with equal selector results are routed to the same partition:

```csharp
options.MaxConcurrency = 4;
options.UsePartitionKey(request => request.OrderId);
```

Keys may be finite integer-valued numbers or non-null strings. The selector should be deterministic, side-effect free,
and safe for concurrent calls.

- Equal keys enter the same partition and are processed in the order they are appended there.
- Concurrent callers do not establish an order before their requests are appended.
- Different keys may land in the same partition; keys and partitions are not one-to-one.
- Partition keys control routing only. They neither force requests for one key into the same batch nor deduplicate them.

### Duplicate Request Merging Example

Merging repeated updates is one use of partition routing. If several `PriceUpdate` requests share a `ProductId`, the
handler can group one batch by product and persist only the highest version. Requests for one product are not handled
concurrently by separate handler calls, while other products may still run on different partitions. Once the batch
write succeeds, every `Task` in that batch completes successfully, including requests superseded by a newer version.

The runnable [duplicate update Web API sample](samples/RequestBatcher.Deduplication) uses PostgreSQL and shows both
safeguards needed for this pattern: the handler retains the highest version per product within one batch, then writes
the winners with one bulk upsert; the database rejects stale versions across batches. The storage check is still
required because a partition key does not place every request for one key in the same batch.

The same sample also batches reads. Each query request carries its result, while the query handler deduplicates product
IDs before issuing one SQL query for the distinct IDs in its batch.

## Completion, Cancellation, and Shutdown

- A successful handler call completes every request in that batch successfully.
- If the handler throws, every request in the batch receives the original exception. The failure is logged once for the
  batch.
- A batch `ProcessAsync` task waits for every submitted request and faults if any handler call involved in that
  submission fails.
- Caller cancellation removes a request only before handler dispatch. After dispatch, the caller observes the actual
  batch outcome so that cancellation cannot hide a completed side effect.
- `StopAsync` and `DisposeAsync` stop admission, then drain all accepted requests. Canceling the token passed to
  `StopAsync` cancels only that wait; shutdown continues in the background.

## Dependency Injection and Logging

`AddRequestBatcher` registers the handler, coordinator, and internal BufferQueue topic in the application's existing
`IServiceCollection`. Applications do not install or configure BufferQueue separately, and RequestBatcher does not
build or own another service provider.

Scoped and transient handlers are resolved once per batch in an asynchronous scope. A singleton handler is reused
across batches and must be thread-safe when `MaxConcurrency > 1`.

Logs use the application's `Microsoft.Extensions.Logging` pipeline under the category
`RequestBatchCoordinator<TRequest>`. Handler and enqueue failures retain the original exception, while request payloads
are never logged. If no logger is registered, RequestBatcher uses `NullLogger`.

## Architecture

![RequestBatcher architecture](docs/assets/request-batcher-architecture.png)

Callers depend only on `IRequestBatcher<TRequest>`. The coordinator accepts single or explicit batch submissions, sends
their requests to internal in-memory partitions, invokes the registered handler for each processing batch, and
completes the caller's `Task` from those outcomes. Solid arrows show request and batch flow; dashed arrows show
completion flow.

## Sample and Development

- [PostgreSQL Web API sample: batched upserts and deduplicated reads](samples/RequestBatcher.Deduplication)
- [Changelog](CHANGELOG.md)
- [Unit tests](tests/RequestBatcher.Tests)

```bash
dotnet build RequestBatcher.slnx --configuration Release
dotnet test tests/RequestBatcher.Tests/RequestBatcher.Tests.csproj --configuration Release
```

## License

RequestBatcher is available under the [MIT License](LICENSE).
