# RequestBatcher

RequestBatcher lets application code submit one request and await one `Task` or `Task<TResponse>`, while an
application handler receives multiple queued requests as an `IReadOnlyList<TRequest>` or
`IReadOnlyList<RequestBatchItem<TRequest, TResponse>>`.

> **Batching does not require callers to assemble a collection.** Separate callers can each submit one `TRequest`
> concurrently. RequestBatcher coalesces requests already queued for the same partition into handler batches of up to
> `BatchSize`. `ProcessAsync(IEnumerable<TRequest>)` is an additional submission option, not a prerequisite for
> batching.

[Full documentation](https://github.com/eventhorizon-cli/RequestBatcher#readme) |
[简体中文](https://github.com/eventhorizon-cli/RequestBatcher/blob/main/README.zh-CN.md)

## APIs

### Request-Only

`IRequestBatcher<TRequest>` returns `Task` after an `IRequestBatchHandler<TRequest>` has processed the submitted
request, or every request in an explicit group.

### Request/Response

`IRequestBatcher<TRequest, TResponse>` returns `Task<TResponse>` for one request or ordered responses for an explicit
group. Its `IRequestBatchHandler<TRequest, TResponse>` assigns exactly one response to each request item.

## When to Use It

Use RequestBatcher for independent database, cache, or downstream operations that benefit from a batch API. It also
fits short traffic bursts that need bounded internal queue capacity and downstream concurrency, related requests that
benefit from batch-level merging without relying on handler ordering, and work where caller cancellation should remove
only requests that have not yet been dispatched. Dispatched work is allowed to finish independently of that caller.

For database updates, if partial success within one handler invocation would leave inconsistent state, the handler
should execute that invocation in one transaction. RequestBatcher propagates the handler outcome but cannot roll back
writes that have already been committed.

## When Not to Use It

Do not use RequestBatcher when work must survive process failure, remain inside the caller's transaction, wait for a
minimum batch size, or rely on automatic retries or exactly-once effects. It is also not
suitable when an in-flight downstream operation must stop as soon as its individual caller disconnects, times out, or
cancels. One handler batch can contain requests from several callers, so caller cancellation tokens are not forwarded
to the handler and cannot cancel the shared handler call.

## Install

```bash
dotnet add package RequestBatcher
```

## Request-Only Batching

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

## Request/Response Batching

Use the response-enabled API when each request should return a value. The handler receives an item containing the
original request and its response slot:

```csharp
public sealed record PriceQuery(long ProductId);
public sealed record PriceQuote(long ProductId, decimal Price);

public interface IPriceStore
{
    // The store returns one entry per input ID, in the same order.
    Task<IReadOnlyList<PriceQuote?>> FindAsync(
        IEnumerable<long> productIds,
        CancellationToken cancellationToken);
}

public sealed class PriceQueryHandler(IPriceStore store)
    : IRequestBatchHandler<PriceQuery, PriceQuote?>
{
    public async ValueTask HandleAsync(
        IReadOnlyList<RequestBatchItem<PriceQuery, PriceQuote?>> items,
        CancellationToken cancellationToken = default)
    {
        var responses = await store.FindAsync(
            items.Select(item => item.Request.ProductId),
            cancellationToken);
        items.SetResponses(responses);
    }
}

services.AddRequestBatcher<PriceQuery, PriceQuote?, PriceQueryHandler>(
    ServiceLifetime.Scoped,
    options => options.BatchSize = 256);

public sealed class PriceService(IRequestBatcher<PriceQuery, PriceQuote?> priceBatcher)
{
    public Task<PriceQuote?> FindAsync(
        long productId,
        CancellationToken cancellationToken = default) =>
        priceBatcher.ProcessAsync(new PriceQuery(productId), cancellationToken);
}
```

The handler must set exactly one response for every item before it returns. `item.SetResponse(response)` sets one item
directly; `items.SetResponses(responses)` maps the nth response in the enumeration to the nth request item. The response
enumeration must have the same order and one entry per item. The caller's `Task<TResponse>` completes only after the
handler succeeds and its item's response has been set.

For a response-enabled batcher, the explicit-group overload returns one response per input request in the same order,
even when the requests are split across handler batches or queue partitions. If several handler calls fail for one
explicit group, `await` follows normal `Task` semantics and throws one original exception. All distinct exception
instances remain available through `Task.Exception.InnerExceptions`; one handler exception fanned out to several
requests is recorded once.

## Explicit Group Submission

When requests already exist as a group, submit them together:

```csharp
await batcher.ProcessAsync(orderWriteRequests, cancellationToken);
```

This overload snapshots the group and submits it as one producer operation, then waits for every request. In `Wait`
mode, an oversized group enters the queue as consecutive capacity-sized slices. In `Fail` mode, the whole group must
fit immediately. The submission does not force one handler call; it may still be split by `BatchSize` or partition
routing.

**An explicit group is not a partition boundary.** With more than one partition, every item is routed independently;
the group is guaranteed to stay in one partition only when `MaxConcurrency = 1`, or when every item produces the same
partition key.

## Submission Behavior

| Behavior | Single request | Explicit group |
| --- | --- | --- |
| Capacity | Reserves one request slot. | `Wait` admits oversized groups in consecutive capacity-sized slices; `Fail` requires the whole group to fit immediately. |
| Routing | Routes one request. | Routes every item independently and can span partitions. |
| Handler calls | May share a handler batch with other queued requests. | Can split by partition and `BatchSize`; it is not a handler-batch boundary. |
| Completion | Represents this request's actual outcome. | Waits for every item in the submission. |
| Failure | Fails when its handler batch fails. | Can contain both successful work and failed work; the returned `Task` faults without rolling back successes. |
| Cancellation | Cancels only before dispatch. | Applies one token to every item; dispatched items still report their actual outcome. |

## Batching and Capacity

- A handler batch contains at most `BatchSize` requests that are already queued.
- RequestBatcher does not add a fixed delay or wait for a minimum batch size.
- `MaxPendingRequests` is the internal BufferQueue capacity for requests that have not yet been pulled. A pulled batch
  is auto-committed before its handler starts, so up to `MaxConcurrency * BatchSize` requests can execute in addition
  to queued capacity. It does not limit the size of one explicit submission or the number of callers waiting for
  capacity.
- `Wait` admits a group no larger than the capacity atomically and splits a larger group into consecutive
  capacity-sized slices. `Fail` rejects the whole submission unless it fits immediately.
- Caller cancellation removes a request only before handler dispatch. After dispatch, the caller observes the real
  handler outcome.
- Shutdown rejects new calls and drains every submission that started before shutdown, including submissions waiting
  for capacity. Requests still cannot be recovered after a process failure.

## Routing and Dispatch

`MaxConcurrency` is the global maximum number of concurrent handler batches. The queue uses
`min(MaxConcurrency, max(1, Environment.ProcessorCount))` internal partitions, and one `BatchDispatchLoop` owns all
of them. It acquires an execution slot before pulling an auto-committed batch, so there is no application-owned
handoff queue behind BufferQueue.

| Configuration | Routing and dispatch |
| --- | --- |
| `MaxConcurrency = 1` | All requests use one queue partition; one handler batch can execute at a time. |
| `MaxConcurrency > 1`, no partition key | Requests advance round-robin across the capped partition count; batches compete for global execution slots. |
| `MaxConcurrency > 1`, with `UsePartitionKey` | The partition-key function runs for every item. Equal keys use one queue partition, but separate handler batches for that key can execute concurrently. |

RequestBatcher provides no global, partition-local, or partition-key handler execution ordering guarantee.

Partition routing is optional:

```csharp
services.AddRequestBatcher<OrderWriteRequest, OrderWriteBatchHandler>(
    ServiceLifetime.Scoped,
    options =>
    {
        options.BatchSize = 256;
        options.MaxConcurrency = 4;

        // Optional. Without a partition-key function, routing is round-robin.
        options.UsePartitionKey(request => request.OrderId);
    });
```

Equal finite, integer-valued numeric keys or equal non-null string keys are routed to one queue partition. A partition
key controls routing only: it does not force related requests into one handler batch, deduplicate them, or serialize
their handler execution.

Handlers can merge repeated updates within the current batch. Correctness across batches still requires application
safeguards such as version checks, idempotency keys, unique constraints, or transactions.

Do not move an operation into RequestBatcher when it must commit or roll back with the caller's transaction.

The runnable
[PostgreSQL Web API sample](https://github.com/eventhorizon-cli/RequestBatcher/tree/main/samples/RequestBatcher.Deduplication)
shows batched upserts, version-protected updates, and deduplicated reads.

## License

[MIT](https://github.com/eventhorizon-cli/RequestBatcher/blob/main/LICENSE)
