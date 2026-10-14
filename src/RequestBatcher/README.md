# RequestBatcher

RequestBatcher is an in-process .NET library for turning individual requests into downstream batch operations. Callers
submit one request at a time and await their own result, while RequestBatcher combines requests that are already queued,
limits handler-batch concurrency and queue capacity, and invokes the application handler with a batch. It is intended for
database, cache, and API operations that support processing multiple items together.

## How Requests Are Batched: Opportunistic Merging

RequestBatcher uses [BufferQueue](https://github.com/eventhorizon-cli/BufferQueue) to combine queued requests within a
.NET process and pass them to an application handler. Callers obtain `IRequestBatcher<TRequest>` or
`IRequestBatcher<TRequest, TResponse>` through DI, submit requests, and await their individual results. The current
dependency is [BufferQueue 2.0.3](https://www.nuget.org/packages/BufferQueue/2.0.3); applications do not register or
configure it separately.

RequestBatcher uses **Opportunistic Merging**. Writing a request to BufferQueue signals that data is available and wakes
the consumer path. When an execution slot is free, RequestBatcher pulls the requests already queued. Consumption is
triggered by a push notification and uses a pull API to retrieve the batch; there is no periodic polling or fixed
collection window. With a free slot, waiting mainly comes from wake-up and scheduling; when all slots are occupied,
requests must wait for capacity to execute. `BatchSize` limits the number of requests per handler call: a batch may
contain just one request at low traffic or several requests already queued in the same partition during a burst.

Requests are held in memory and cannot be recovered after a process failure.

> **Batching does not require callers to assemble a collection.** Separate callers can each submit one `TRequest`
> concurrently. RequestBatcher coalesces requests already queued for the same partition into handler batches of up to
> `BatchSize`. `ProcessAsync(IEnumerable<TRequest>)` is an additional submission option, not a prerequisite for
> batching.

[Full documentation](https://github.com/eventhorizon-cli/RequestBatcher#readme) |
[简体中文](https://github.com/eventhorizon-cli/RequestBatcher/blob/main/README.zh-CN.md)

## Installation and DI Registration

```bash
dotnet add package RequestBatcher
```

The package supports .NET 8 and .NET 10 applications through `net8.0` and `net10.0` assets.

Register the handler and batcher with `AddRequestBatcher` at startup. At the call site, resolve
`IRequestBatcher<TRequest>` from the same DI container or inject it into an application service's constructor.

```csharp
// At startup: register with the application's IServiceCollection.
services.AddRequestBatcher<OrderWriteRequest, OrderWriteBatchHandler>(
    ServiceLifetime.Scoped,
    options =>
    {
        // Four concurrent handler batches, shared by all eight queue partitions.
        options.MaxConcurrency = 4;
        options.BatchSize = 100;
        options.MaxPendingRequests = 5_000;
        // Equal order IDs route to the same partition; separate batches can still run concurrently.
        options.UsePartitionKey(request => request.OrderId, partitionCount: 8);
    });

// At the call site: resolve from that application's service provider.
var batcher = serviceProvider.GetRequiredService<IRequestBatcher<OrderWriteRequest>>();
await batcher.ProcessAsync(new OrderWriteRequest(OrderId: 1, Amount: 100m));
```

Use `Microsoft.Extensions.DependencyInjection` and `RequestBatcher`. In application services, constructor-inject
`IRequestBatcher<TRequest>` instead of resolving it manually. For typed results, register with
`AddRequestBatcher<TRequest, TResponse, THandler>` and inject `IRequestBatcher<TRequest, TResponse>`.
See the [request and handler definitions](#request-only-batching) below, and register the handler's dependencies
(such as your `IOrderStore` implementation) in the same container.

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

## APIs

### Request-Only

`IRequestBatcher<TRequest>` returns `Task` after an `IRequestBatchHandler<TRequest>` has processed the submitted
request, or every request in an explicit group:

```csharp
public interface IRequestBatcher<in TRequest>
{
    // Submit one request and wait for its actual handler outcome.
    Task ProcessAsync(TRequest request, CancellationToken cancellationToken = default);

    // Submit an existing group; the task waits for every request in that submission.
    Task ProcessAsync(
        IEnumerable<TRequest> requests,
        CancellationToken cancellationToken = default);
}

public interface IRequestBatchHandler<TRequest>
{
    // Process one handler batch. A successful return completes every request in the batch.
    ValueTask HandleAsync(
        IReadOnlyList<TRequest> requests,
        CancellationToken cancellationToken = default);
}
```

### Request/Response

`IRequestBatcher<TRequest, TResponse>` returns `Task<TResponse>` for one request or `Task<IReadOnlyList<TResponse>>`
for an explicit group, with responses in input order. Its `IRequestBatchHandler<TRequest, TResponse>` assigns exactly
one response to each request item and returns `ValueTask`:

```csharp
public interface IRequestBatcher<in TRequest, TResponse>
{
    // Submit one request and return its typed response.
    Task<TResponse> ProcessAsync(
        TRequest request,
        CancellationToken cancellationToken = default);

    // Submit a group and return responses in the same order as the input sequence.
    Task<IReadOnlyList<TResponse>> ProcessAsync(
        IEnumerable<TRequest> requests,
        CancellationToken cancellationToken = default);
}

public interface IRequestBatchHandler<TRequest, TResponse>
{
    // Process one batch and assign exactly one response to every entry.
    ValueTask HandleAsync(
        IReadOnlyList<RequestBatchItem<TRequest, TResponse>> entries,
        CancellationToken cancellationToken = default);
}
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
        options.MaxConcurrency = 4;
        options.BatchSize = 256;
        options.MaxPendingRequests = 5_000;
        options.UsePartitionKey(request => request.OrderId, partitionCount: 8);
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

Use the response-enabled API when each request should return a value. The handler receives entries that wrap the
original request and its response slot; `entry.Request` is the original `TRequest`. This example deduplicates repeated
product IDs within the current handler batch before querying the store:

```csharp
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RequestBatcher;

public sealed record PriceQuery(long ProductId);
public sealed record PriceQuote(long ProductId, decimal Price);

public interface IPriceStore
{
    // The store returns at most one found quote per requested ID, in any order.
    Task<IReadOnlyList<PriceQuote>> FindAsync(
        IEnumerable<long> productIds,
        CancellationToken cancellationToken);
}

public sealed class PriceQueryHandler(IPriceStore store)
    : IRequestBatchHandler<PriceQuery, PriceQuote?>
{
    public async ValueTask HandleAsync(
        IReadOnlyList<RequestBatchItem<PriceQuery, PriceQuote?>> entries,
        CancellationToken cancellationToken = default)
    {
        var keys = entries
            .Select(entry => entry.Request.ProductId)
            .Distinct()
            .ToArray();
        var prices = await store.FindAsync(
            keys,
            cancellationToken);
        var pricesByProductId = prices.ToDictionary(price => price.ProductId);

        entries.SetResponses(request => pricesByProductId.GetValueOrDefault(request.ProductId));

        // If query deduplication is unnecessary, remove Distinct() and keep the response factory.
        // Alternatively, replace the query and assignment above with ONE of these styles:
        // 1. Query all requested IDs, then assign each entry directly:
        // var prices = await store.FindAsync(entries.Select(entry => entry.Request.ProductId), cancellationToken);
        // var pricesByProductId = prices.ToDictionary(price => price.ProductId);
        // foreach (var entry in entries)
        // {
        //     entry.SetResponse(pricesByProductId.GetValueOrDefault(entry.Request.ProductId));
        // }
        // 2. If an already aligned response sequence is available, use the ordered overload:
        // var prices = await store.FindAsync(entries.Select(entry => entry.Request.ProductId), cancellationToken);
        // var pricesByProductId = prices.ToDictionary(price => price.ProductId);
        // var orderedResponses = entries
        //     .Select(entry => pricesByProductId.GetValueOrDefault(entry.Request.ProductId))
        //     .ToArray();
        // entries.SetResponses(orderedResponses);
        // Never pass raw backend results to the ordered overload: they may be unordered or omit missing keys.
    }
}

public sealed class PriceService(IRequestBatcher<PriceQuery, PriceQuote?> priceBatcher)
{
    public Task<PriceQuote?> FindAsync(
        long productId,
        CancellationToken cancellationToken = default) =>
        priceBatcher.ProcessAsync(new PriceQuery(productId), cancellationToken);

    public Task<IReadOnlyList<PriceQuote?>> FindManyAsync(
        IEnumerable<PriceQuery> requests,
        CancellationToken cancellationToken = default) =>
        priceBatcher.ProcessAsync(requests, cancellationToken);
}
```

Register your application's `IPriceStore` implementation (shown as `PriceStore`) and the batcher in the existing
service collection, then inject `IRequestBatcher<PriceQuery, PriceQuote?>` into application services. The runnable
[PostgreSQL Web API sample](https://github.com/eventhorizon-cli/RequestBatcher/tree/main/samples/RequestBatcher.Deduplication) implements the same lookup flow directly with Dapper and PostgreSQL.

```csharp
using Microsoft.Extensions.DependencyInjection;
using RequestBatcher;

services.AddScoped<IPriceStore, PriceStore>();
services.AddRequestBatcher<PriceQuery, PriceQuote?, PriceQueryHandler>(
    ServiceLifetime.Scoped,
    options =>
    {
        options.MaxConcurrency = 4;
        options.BatchSize = 100;
        options.MaxPendingRequests = 5_000;
        options.UsePartitionKey(query => query.ProductId, partitionCount: 8);
    });
```

The handler must set exactly one response for every item before it returns. The example uses standard LINQ `Distinct`
to query each product ID once within the current handler invocation. `Distinct` also supports anonymous composite keys,
value tuples, and custom equality comparers. Missing product IDs are absent from the store result and map to `null`.
It does not combine entries across handler batches or queue partitions. The selector overload evaluates the raw request
for every entry after the unordered backend result has been indexed by key, so duplicate entries receive the same response.
All responses are computed before assignment; a factory failure leaves unset entries unchanged. Reference-type results
are shared, not cloned.

If deduplication is unnecessary, remove `Distinct()` and keep the same response factory. Alternatively, assign entries
individually with `entry.SetResponse(response)`, or use the ordered `entries.SetResponses(responses)` overload after producing exactly one response per original entry in input order,
including duplicates and missing requests. The keyed selector overload is recommended for unordered backend results;
the ordered overload must receive the aligned sequence, never raw backend rows. The explicit-group overload returns
responses in input order even when RequestBatcher splits the requests across handler batches or queue partitions.
The caller's `Task<TResponse>` completes only after the
handler succeeds, its item's response has been set, and its handler scope has been disposed.

Non-cancellation handler failures propagate the original exception. A handler `OperationCanceledException` produces a
canceled response task. For a single response, awaiting it throws `TaskCanceledException` with the original token;
the cancellation exception instance, message, and derived type are not preserved.

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
the group stays in one partition when no partition key is configured or when every item produces the same partition
key. `MaxConcurrency = 1` limits handler concurrency but does not merge keyed partitions.

## Submission Behavior

| Behavior | Single request | Explicit group |
| --- | --- | --- |
| Capacity | Reserves one request slot. | `Wait` admits oversized groups in consecutive capacity-sized slices; `Fail` requires the whole group to fit immediately. |
| Routing | Routes one request. | Routes every item independently and can span partitions. |
| Handler calls | May share a handler batch with other queued requests. | Can split by partition and `BatchSize`; it is not a handler-batch boundary. |
| Completion | Represents this request's actual outcome. | Waits for every item in the submission. |
| Failure | Fails when its handler batch fails. | Can contain both successful work and failed work; the returned `Task` faults without rolling back successes. |
| Cancellation | Cancels only before dispatch. | Applies one token to every item; dispatched items still report their actual outcome. |

## Configuration

| Option | Default | Behavior |
| --- | ---: | --- |
| `BatchSize` | `128` | Maximum requests passed to one handler call. |
| `MaxConcurrency` | `1` | Maximum concurrent handler batches. It is independent of the keyed partition count. |
| `MaxPendingRequests` | `8192` | Internal BufferQueue capacity for requests that have not yet been pulled for execution. |
| `FullMode` | `Wait` | Waits for capacity; `Fail` rejects immediately when capacity is unavailable. |
| `UsePartitionKey(selector, partitionCount)` | Not set | Enables keyed routing with the explicitly configured partition count; `partitionCount` must be greater than one and equal keys use one partition. |

No ordering is guaranteed for handler execution, including when `MaxConcurrency = 1`.

### Partition Count and Concurrency

This configuration uses eight queue partitions sharing four handler execution slots:

```csharp
builder.Services.AddRequestBatcher<ProductQueryItem, ProductBatchHandler>(
    ServiceLifetime.Scoped,
    options =>
    {
        // Per-pod downstream limit. Six pods x four slots = at most 24 handler batches cluster-wide.
        options.MaxConcurrency = 4;
        options.BatchSize = 100;
        options.MaxPendingRequests = 5_000;

        // Routing/locality only. Eight queue partitions are independent of the four execution slots.
        options.UsePartitionKey(item => item.ProductId, partitionCount: 8);
    });
```

`partitionCount` controls how requests are distributed across queues; equal keys always route to the same partition.
`MaxConcurrency` limits concurrent handler batches for one registered batcher, with all partitions sharing those slots.
A single partition can also supply several concurrent batches.

### Sizing `MaxConcurrency` with Little's Law

Use Little's Law on handler-batch traffic, not directly on item traffic. For example, if one handler batch issues one
downstream batch query, use the batch-query QPS (`λ_batch`, queries per second) and its mean response time (`RT`, in
seconds). For an existing operation, obtain them from monitoring metrics; for a new operation, use the expected query QPS
and target response time or a load-test result as the initial estimate. Keep the QPS and response time for the same query
operation:

```text
Concurrency (in flight) = λ_batch × RT
L = λ x W
```

Here, `L` is the number of in-flight handler batches, `λ` is their arrival rate, and `W` is their mean response time.

If the batch-query QPS and response time are available, estimate the cluster-wide in-flight batch count with
`L = λ_batch × RT`. Divide by the number of active pods and round up, then add a small safety margin. Expected values
produce a starting configuration; replace them with production metrics after the feature is running.

```text
MaxConcurrency_per_pod ≈ ceil(λ_batch × RT / pod_count × headroom)
```

For example, six active pods handling 1,920 downstream batch calls/second at a 50 ms mean response time need about
`1,920 × 0.050 = 96` in-flight batch calls. With evenly distributed traffic, `MaxConcurrency = 16` gives 16 slots per pod
and 96 slots across six pods. This estimates average load; it does not guarantee latency or throughput. Adjust the limit
using measured queue wait, downstream throttling, CPU, and connection limits.

When QPS is measured per item, estimate the effective batch size separately for each partition. For partition `p`, let
`λ_item,p` be its item QPS, `B_eff,p` its measured average items per handler call, and `RT_p` its mean handler response time:

```text
λ_batch,p ≈ λ_item,p / B_eff,p
L_total ≈ Σ(λ_batch,p × RT_p)
MaxConcurrency_per_pod ≈ ceil(L_total / pod_count × headroom)
```

`BatchSize` is only an upper bound, so `B_eff,p` can be much smaller when traffic is sparse. `partitionCount` changes the
item rate seen by each partition and therefore can change `B_eff,p`; it is not a worker count and must not be multiplied
directly into the formula. Increasing the partition count can reduce batch sizes, while increasing `BatchSize` can increase
batch size and response time together. A larger partition count can also reduce opportunistic deduplication because each
partition sees fewer requests at a time. If the deduplication key is the same as the partition key, equal keys still route
to one partition, but separate handler batches can still miss each other. If the keys differ, requests for one deduplication
target can be routed to different partitions. Choose the smallest partition count that isolates hot keys, then verify the
effective batch size and deduplication hit rate after changing either setting.

If batch-call QPS is available, use the measured value directly instead of estimating it from item QPS and batch size.

`MaxPendingRequests` is a backlog limit, not another execution slot count. Choose it from the queueing you are willing to
absorb (`item QPS per batcher × acceptable queue time`), then verify admission latency and memory use under bursts.

## Batch Size and Capacity

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
- `DisposeAsync` rejects new calls, drains accepted submissions including those waiting for capacity, and releases
  coordinator-owned resources. Concurrent and repeated calls share one operation; the application container owns
  async disposal. Requests still cannot be recovered after a process failure.

## Routing Rules and Dispatch

The queue rotates pull opportunities among readable partitions. This does not promise equal handler execution time
or fairness between individual keys. String routing considers the complete key, so a shared prefix alone does not
force different keys into one partition. Exact partition assignments are internal and may change between versions;
equal keys still route together within a batcher instance. Such changes can affect batch composition and the amount
of duplicate work merged inside a handler batch.

A handler runs on the dispatch call path until it completes or reaches an incomplete asynchronous wait. CPU-heavy
or synchronously blocking work before that point can delay other batches even when execution slots remain free.
`MaxConcurrency` is an upper bound; it does not automatically parallelize synchronous handler code.

The queue partition count is not the handler concurrency level. Partitions organize queueing and routing, while
`MaxConcurrency` sets the global maximum number of concurrent handler batches. Without a partition key, the queue uses
one internal partition to avoid fragmenting unrelated requests. With a finite integer numeric key or a non-null string
key, it uses the explicit count supplied to `UsePartitionKey`, so equal keys retain locality while different keys can
progress independently. Multiple batches from one partition can execute concurrently, and all partitions share the same
execution slots. One `BatchDispatchLoop` owns them all. It acquires an execution slot before pulling an auto-committed
batch, so there is no application-owned handoff queue behind BufferQueue. Each handler batch releases its slot independently
when it finishes, keeping at most `MaxConcurrency` handler batches in flight. The partition count is fixed at registration
and independent of CPU count.

| Configuration | Routing and dispatch |
| --- | --- |
| No `UsePartitionKey` | All requests use one queue partition; one partition can supply multiple asynchronous handler batches that compete for global execution slots. |
| `UsePartitionKey(selector, partitionCount)` | The partition-key function runs for every item. Equal finite integer numeric keys or non-null string keys use one of the configured partitions; separate handler batches for an equal key can execute concurrently. |

RequestBatcher provides no global, partition-local, or partition-key handler execution ordering guarantee.

Partition routing is optional:

```csharp
services.AddRequestBatcher<OrderWriteRequest, OrderWriteBatchHandler>(
    ServiceLifetime.Scoped,
    options =>
    {
        options.BatchSize = 256;
        options.MaxConcurrency = 4;

        // Optional. Without a partition-key function, all requests use one shared partition.
        options.UsePartitionKey(request => request.OrderId, partitionCount: 8);
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
