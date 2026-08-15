# RequestBatcher

Transparent request batching for concurrent .NET workloads.

[Full documentation](https://github.com/eventhorizon-cli/RequestBatcher#readme) |
[简体中文](https://github.com/eventhorizon-cli/RequestBatcher/blob/main/README.zh-CN.md)

RequestBatcher lets callers submit one request or an existing group and await one `Task`, while a handler processes the
queued requests as `IReadOnlyList<TRequest>` batches. It is designed for in-process workloads such as database writes,
cache operations, and downstream bulk APIs.

## Install

```bash
dotnet add package RequestBatcher
```

## Usage

Define a request and its batch handler:

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

Register the handler and choose its lifetime in the same call:

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

Submit requests individually:

```csharp
var batcher = serviceProvider.GetRequiredService<IRequestBatcher<OrderWriteRequest>>();

await batcher.ProcessAsync(
    new OrderWriteRequest(OrderId: 42, Amount: 99.50m),
    cancellationToken);
```

Or enqueue an existing group with one production operation:

```csharp
await batcher.ProcessAsync(orderWriteRequests, cancellationToken);
```

The returned `Task` completes after all requests from that call have finished. A successful handler call completes every
request in that handler batch; if it throws, those requests receive the original exception. One explicit submission may
still be split across partitions or handler calls according to `BatchSize`.

## Key Behavior

- Batches contain at most `BatchSize` requests. RequestBatcher does not wait for a minimum batch size or add a fixed
  batching delay.
- `MaxConcurrency` limits concurrent handler calls. The default value of `1` preserves global FIFO processing.
- `MaxPendingRequests` bounds accepted work. `FullMode` can wait asynchronously for capacity or return a faulted `Task`
  immediately.
- Explicit batches reserve capacity as one unit and cannot exceed `MaxPendingRequests`; insufficient capacity never
  causes a partial submission.
- `UsePartitionKey` routes equal numeric or string keys to one partition. It controls routing and partition-local order;
  it does not place every request for one key in the same batch or deduplicate requests.
- Caller cancellation removes a request only before handler dispatch. Once dispatch starts, the caller receives the
  actual batch outcome.
- Accepted requests are drained during shutdown. Pending requests are held only in memory and cannot be recovered after
  a process failure.

RequestBatcher does not retry failed handlers or provide exactly-once side effects. Keep operations idempotent when they
may be retried, and do not move work out of a transaction when it must commit or roll back with that transaction.

A runnable [PostgreSQL Web API sample](https://github.com/eventhorizon-cli/RequestBatcher/tree/main/samples/RequestBatcher.Deduplication)
shows partitioned duplicate-update merging and one bulk upsert per handler batch.

## License

[MIT](https://github.com/eventhorizon-cli/RequestBatcher/blob/main/LICENSE)
