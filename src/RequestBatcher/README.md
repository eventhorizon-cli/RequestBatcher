# RequestBatcher

RequestBatcher collects concurrent requests inside a .NET process and invokes an application handler with
`IReadOnlyList<TRequest>` batches. Callers continue to submit one request at a time and await a normal `Task`.

[Full documentation](https://github.com/eventhorizon-cli/RequestBatcher#readme) |
[简体中文](https://github.com/eventhorizon-cli/RequestBatcher/blob/main/README.zh-CN.md)

RequestBatcher keeps pending requests in memory. It does not provide durable delivery, automatic retries, or
exactly-once effects.

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

When requests already exist as a group, submit them together:

```csharp
await batcher.ProcessAsync(orderWriteRequests, cancellationToken);
```

This overload snapshots and admits the group as one capacity unit, then waits for every request. It does not force one
handler call; the group may still be split by `BatchSize` or partition routing.

## Batching Model

- A handler batch contains at most `BatchSize` requests that are already queued.
- RequestBatcher does not add a fixed delay or wait for a minimum batch size.
- `MaxConcurrency` limits concurrent handler calls. Its default value of `1` preserves global FIFO processing.
- `MaxPendingRequests` bounds accepted work. `FullMode` either waits for capacity or rejects immediately.
- Caller cancellation removes a request only before handler dispatch. After dispatch, the caller observes the real
  handler outcome.
- Accepted requests are drained during shutdown, but they cannot be recovered after a process failure.

## Optional Partition Routing

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
