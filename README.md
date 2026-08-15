# RequestBatcher

[![NuGet](https://img.shields.io/nuget/v/RequestBatcher.svg)](https://www.nuget.org/packages/RequestBatcher)
[![Build](https://github.com/eventhorizon-cli/RequestBatcher/actions/workflows/dotnet-build.yml/badge.svg)](https://github.com/eventhorizon-cli/RequestBatcher/actions/workflows/dotnet-build.yml)
[![Codecov](https://codecov.io/gh/eventhorizon-cli/RequestBatcher/graph/badge.svg)](https://codecov.io/gh/eventhorizon-cli/RequestBatcher)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

**English** | [简体中文](README.zh-CN.md)

RequestBatcher collects concurrent requests inside a .NET process and passes them to an application handler in
batches. Each caller submits a normal request and awaits a `Task`; the handler can then use one database command,
cache operation, or downstream call for multiple requests.

RequestBatcher is an in-memory coordination component. It does not persist requests or retry failed handlers. Work
submitted to it is not part of the caller's existing transaction.

## How It Works

1. A caller submits one request through `ProcessAsync`.
2. RequestBatcher admits the request according to the configured capacity and routes it to an in-memory partition.
3. When that partition is ready, RequestBatcher takes up to `BatchSize` requests that are already queued and invokes
   the handler once.
4. The handler outcome completes the `Task` returned to every caller represented in that handler batch.

`BatchSize` is an upper bound, not a minimum. RequestBatcher does not hold the first request for a fixed batching
window, so low traffic may produce single-request batches while concurrent traffic naturally produces larger batches.

![RequestBatcher architecture](docs/assets/request-batcher-architecture.png)

The two sides of the API have different responsibilities:

| Side | API | Meaning |
| --- | --- | --- |
| Caller | `ProcessAsync(TRequest)` | Submits one request and returns a `Task` for that request's actual outcome. |
| Caller | `ProcessAsync(IEnumerable<TRequest>)` | Submits an existing group and returns one `Task` that waits for the whole submission. |
| Handler | `HandleAsync(IReadOnlyList<TRequest>)` | Processes one batch selected by RequestBatcher and returns a `ValueTask` as its completion signal. |

The handler's `ValueTask` is awaited once inside RequestBatcher and is never returned to the caller. It allows a
handler that completes synchronously to avoid allocating a `Task`; regular asynchronous I/O can still be implemented
with `async ValueTask`.

## Installation

```bash
dotnet add package RequestBatcher
```

## Quick Start

Define the request and the code that handles one batch:

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

Register the handler and RequestBatcher together. The handler lifetime is part of the registration; the minimal setup
only needs a batch size:

```csharp
services.AddRequestBatcher<OrderWriteRequest, OrderWriteBatchHandler>(
    ServiceLifetime.Scoped,
    options =>
    {
        options.BatchSize = 256;
    });
```

Inject `IRequestBatcher<TRequest>` into the normal application call path:

```csharp
public sealed class OrderService(IRequestBatcher<OrderWriteRequest> batcher)
{
    public Task SaveAsync(
        OrderWriteRequest request,
        CancellationToken cancellationToken = default) =>
        batcher.ProcessAsync(request, cancellationToken);
}
```

`SaveAsync` returns the same `Task` produced by RequestBatcher. It completes only after the handler has processed the
request; the caller does not need to know which batch or partition contained it. A handler delegate can also be
registered when a separate handler class is unnecessary.

### Submitting an Existing Group

When the caller already has multiple requests, it can submit them with one call:

```csharp
await batcher.ProcessAsync(orderWriteRequests, cancellationToken);
```

RequestBatcher snapshots the sequence and admits it as one capacity unit. The returned `Task` waits for every request
from that submission. This does not force the sequence into one handler call: it may still be split by `BatchSize` or
partition routing.

## Processing Semantics

### Completion and Failure

- A successful handler call completes every caller represented in that handler batch.
- If the handler throws, those callers receive the original exception.
- RequestBatcher does not retry a failed handler.
- The batch overload waits for the whole submitted group and faults if any handler call involved in that submission
  fails.

### Caller Cancellation

Caller cancellation can remove a request only before handler dispatch. Once the handler starts, RequestBatcher reports
the real handler outcome instead of changing the caller's `Task` to canceled. This prevents cancellation from hiding a
side effect that may already have happened.

The token passed to the handler belongs to RequestBatcher's processing lifetime, not to an individual caller. One
caller's cancellation therefore cannot cancel work for the other requests in the same batch.

### Capacity and Backpressure

`MaxPendingRequests` limits accepted requests that have not finished:

- `FullMode = Wait` waits asynchronously for enough capacity and honors caller cancellation while waiting.
- `FullMode = Fail` immediately returns a faulted `Task` with `RequestBatchQueueFullException`.
- An explicit group reserves capacity atomically. RequestBatcher either admits the whole group or none of it.
- A group larger than `MaxPendingRequests` is always rejected.

### Shutdown

`StopAsync` and `DisposeAsync` stop accepting new requests and drain requests that were already accepted. Canceling
the token passed to `StopAsync` stops only that wait; shutdown continues in the background.

## Configuration

| Option | Default | Behavior |
| --- | ---: | --- |
| `BatchSize` | `128` | Maximum requests passed to one handler call. |
| `MaxConcurrency` | `1` | Maximum concurrent handler calls and number of processing partitions. |
| `MaxPendingRequests` | `8192` | Maximum accepted requests that have not completed, and maximum size of one explicit submission. |
| `FullMode` | `Wait` | Waits for capacity; `Fail` rejects immediately when capacity is unavailable. |
| `UsePartitionKey(...)` | Not set | Uses round-robin routing by default; equal selected keys are routed to one partition. |

With `MaxConcurrency = 1`, requests are processed in global FIFO order. With a higher value, handler calls may run in
parallel and ordering is guaranteed only within each partition.

## Partition Keys

A partition key is an optional routing rule for related requests:

```csharp
options.MaxConcurrency = 4;
options.UsePartitionKey(request => request.OrderId);
```

Equal finite, integer-valued numeric keys or equal non-null string keys are routed to the same partition and processed
there in append order. Different keys can still share a partition, so keys and partitions are not one-to-one.

Partition routing does not force all requests for one key into the same handler batch, and it does not deduplicate
requests. It provides an ordering boundary that a handler can use for patterns such as merging repeated updates.

### Example: Merge Repeated Updates

Suppose several `PriceUpdate` requests have the same `ProductId`. A handler can group its current batch by product and
write only the highest version. Routing by `ProductId` prevents separate handler calls from processing that product
concurrently, while other products can still be processed in parallel.

The storage layer must still prevent stale versions from overwriting newer state across batches because a partition key
does not place every update for one product into the same batch. The runnable
[PostgreSQL Web API sample](samples/RequestBatcher.Deduplication) demonstrates both safeguards:

- the write handler merges repeated product updates within one batch, then performs one bulk upsert;
- the upsert condition ignores older versions that arrive in later batches;
- the query handler deduplicates product IDs before issuing one SQL query for the batch.

## Appropriate Uses

- Independent database writes that can use batch `INSERT`, `UPDATE`, or `UPSERT`.
- Cache reads or writes and downstream APIs that already accept multiple items.
- Short traffic bursts where queue capacity and handler concurrency should be bounded.
- Related requests that benefit from partition-local ordering or batch-level merging.

## Avoid It When

- Accepted work must survive process failure. Use durable storage or a reliable message broker.
- The operation must commit or roll back with the caller's transaction. Keep it in that transaction, or record
  independent follow-up work through a transactional outbox.
- Every call needs a direct `TResult`. RequestBatcher returns completion through `Task`; a batched query must carry its
  own result holder or update application state.
- The downstream operation requires automatic retries or exactly-once effects. RequestBatcher provides neither.
- The handler requires a minimum batch size or a fixed collection window. RequestBatcher dispatches currently queued
  work without waiting to fill a batch.

## Dependency Injection and Logging

`AddRequestBatcher` registers the handler, coordinator, and internal BufferQueue topic in the application's existing
`IServiceCollection`. The application does not configure BufferQueue directly, and RequestBatcher never builds a
nested service provider.

Scoped and transient handlers are resolved once per handler batch in an asynchronous scope. A singleton handler is
reused across batches and must be thread-safe when `MaxConcurrency > 1`.

Logs use the application's `Microsoft.Extensions.Logging` pipeline under the category
`RequestBatchCoordinator<TRequest>`. Handler failures are logged with their original exception, and request payloads
are not logged.

## Sample and Development

- [PostgreSQL Web API sample](samples/RequestBatcher.Deduplication)
- [Changelog](CHANGELOG.md)
- [Unit tests](tests/RequestBatcher.Tests)

```bash
dotnet build RequestBatcher.slnx --configuration Release
dotnet test tests/RequestBatcher.Tests/RequestBatcher.Tests.csproj --configuration Release
```

## License

RequestBatcher is available under the [MIT License](LICENSE).
