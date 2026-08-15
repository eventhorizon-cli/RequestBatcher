# RequestBatcher

RequestBatcher lets application code submit one request and await one `Task`, while an application handler receives
multiple queued requests as an `IReadOnlyList<TRequest>`.

[Full documentation](https://github.com/eventhorizon-cli/RequestBatcher#readme) |
[简体中文](https://github.com/eventhorizon-cli/RequestBatcher/blob/main/README.zh-CN.md)

## When to Use It

Use RequestBatcher for independent database, cache, or downstream operations that benefit from a batch API. It also
fits short traffic bursts that need bounded pending work, or related requests that benefit from partition-local order.

## When Not to Use It

Do not use RequestBatcher when work must survive process failure, remain inside the caller's transaction, return a
direct `TResult`, wait for a minimum batch size, or rely on automatic retries or exactly-once effects.

## Install

```bash
dotnet add package RequestBatcher
```

## Usage

Define a request and a handler for one batch:

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

The handler's `ValueTask` is an internal completion signal that RequestBatcher awaits once. Application callers always
receive `Task`.

Register the handler and choose its lifetime:

```csharp
services.AddRequestBatcher<OrderWriteRequest, OrderWriteBatchHandler>(
    ServiceLifetime.Scoped,
    options =>
    {
        options.BatchSize = 256;
    });
```

Inject `IRequestBatcher<TRequest>` and submit requests:

```csharp
public sealed class OrderService(IRequestBatcher<OrderWriteRequest> batcher)
{
    public Task SaveAsync(
        OrderWriteRequest request,
        CancellationToken cancellationToken = default) =>
        batcher.ProcessAsync(request, cancellationToken);
}
```

The returned `Task` completes after the handler has processed that request. If the handler fails, the caller receives
the original exception.

If several handler calls fail for one explicit group, `await` follows normal `Task` semantics and throws one original
exception. All distinct exception instances remain available through `Task.Exception.InnerExceptions`; one handler
exception fanned out to several requests is recorded once.

When requests already exist as a group, submit them together:

```csharp
await batcher.ProcessAsync(orderWriteRequests, cancellationToken);
```

This overload snapshots and admits the group as one capacity unit, then waits for every request. It does not force one
handler call; the group may still be split by `BatchSize` or partition routing.

## Submission Behavior

| Behavior | Single request | Explicit group |
| --- | --- | --- |
| Capacity | Reserves one request slot. | Reserves the whole group atomically. |
| Routing | Routes one request. | Routes every item independently and can span partitions. |
| Handler calls | May share a handler batch with other queued requests. | Can split by partition and `BatchSize`; it is not a handler-batch boundary. |
| Completion | Represents this request's actual outcome. | Waits for every item in the submission. |
| Failure | Fails when its handler batch fails. | Can contain both successful work and failed work; the returned `Task` faults without rolling back successes. |
| Cancellation | Cancels only before dispatch. | Applies one token to every item; dispatched items still report their actual outcome. |

## Batching and Capacity

- A handler batch contains at most `BatchSize` requests that are already queued.
- RequestBatcher does not add a fixed delay or wait for a minimum batch size.
- `MaxPendingRequests` bounds accepted work. `FullMode` either waits for capacity or rejects immediately.
- Caller cancellation removes a request only before handler dispatch. After dispatch, the caller observes the real
  handler outcome.
- Accepted requests are drained during shutdown, but they cannot be recovered after a process failure.

## Routing Modes

`MaxConcurrency` controls both handler concurrency and partition count:

| Configuration | Routing and ordering |
| --- | --- |
| `MaxConcurrency = 1` | All requests use one partition and retain global append order. |
| `MaxConcurrency > 1`, no partition key | Requests advance round-robin, including every item in an explicit group. Ordering is per partition. |
| `MaxConcurrency > 1`, with `UsePartitionKey` | The selector runs for every item. Equal keys use one partition and retain their input order there. |

One explicit group can therefore be handled concurrently by several partitions. Each handler invocation reads from one
partition. Concurrent callers have no defined order before their requests are appended.

Partition routing is optional:

```csharp
services.AddRequestBatcher<OrderWriteRequest, OrderWriteBatchHandler>(
    ServiceLifetime.Scoped,
    options =>
    {
        options.BatchSize = 256;
        options.MaxConcurrency = 4;

        // Optional. Without this selector, routing is round-robin.
        options.UsePartitionKey(request => request.OrderId);
    });
```

Equal finite, integer-valued numeric keys or equal non-null string keys are routed to one partition and processed there
in append order. A partition key controls routing only: it does not force related requests into one handler batch or
deduplicate them.

Partition-local ordering can support patterns such as merging repeated updates within each batch. Correctness across
batches still requires application safeguards such as version checks, idempotency keys, unique constraints, or
transactions.

Do not move an operation into RequestBatcher when it must commit or roll back with the caller's transaction.

The runnable
[PostgreSQL Web API sample](https://github.com/eventhorizon-cli/RequestBatcher/tree/main/samples/RequestBatcher.Deduplication)
shows batched upserts, version-protected updates, and deduplicated reads.

## License

[MIT](https://github.com/eventhorizon-cli/RequestBatcher/blob/main/LICENSE)
