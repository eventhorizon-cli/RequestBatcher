# RequestBatcher

RequestBatcher is an in-process .NET library with **native support for single and batch requests**. Callers submit
one request with `ExecuteAsync(request)` or a batch with `ExecuteAsync(requests)` and await the corresponding results.

RequestBatcher combines requests that are already queued, limits handler-batch concurrency and queue capacity, and
invokes the application handler with a batch. It is intended for database, cache, and API operations that support
processing multiple items together.

![Request coalescing illustration](https://raw.githubusercontent.com/eventhorizon-cli/RequestBatcher/main/docs/assets/request-batcher-request-coalescing.png)

Separate concurrent requests can be combined into one batch before a downstream batch operation. The illustration is
an example: only requests already queued in the same partition can be combined into one handler batch.

**One batch API call does not imply one handler invocation.** Below, A and B each make one batch API call,
submitting queries for products `[101, 102, 103]` and `[102, 103, 104]` respectively.

![Explicit batch splitting and handler deduplication](https://raw.githubusercontent.com/eventhorizon-cli/RequestBatcher/main/docs/assets/request-batcher-explicit-batch-merging.png)

Requests route individually by `ProductId`. In this example, 101 and 103 share one partition, while 102 and 104 share
another. Each partition combines queued requests from both submissions into a handler batch.

**Each handler deduplicates within its own batch.** The two handlers query `[101, 103]` and `[102, 104]` respectively,
then assign responses by `ProductId` to their own requests: four products are queried, and all six requests get a result.

This is one possible grouping. Actual batches depend on enqueue timing, `BatchSize`, and scheduling; equal keys are
not guaranteed to arrive in one batch.

## How Requests Are Batched: Opportunistic Merging

The illustration above shows how requests may be combined. This section explains how those handler batches are formed.

RequestBatcher uses [BufferQueue](https://github.com/eventhorizon-cli/BufferQueue) to combine queued requests within a
.NET process and pass them to an application handler. The current dependency is
[BufferQueue 2.0.3](https://www.nuget.org/packages/BufferQueue/2.0.3); applications do not register or configure it
separately.

This is **opportunistic batching**: writing a request to BufferQueue sends a push notification that wakes an idle
consumer. Once it has an execution slot, the consumer pulls requests queued at that moment; it does not wait for a
fixed time window to fill `BatchSize`.

Callers obtain `IRequestBatcher<TRequest>` or `IRequestBatcher<TRequest, TResponse>` through DI, submit requests, and
await their individual results.

After dispatching a batch, the loop continues. If another slot is available, it can pull and dispatch the next batch
without waiting for the previous batch to finish.

**It waits for the next push notification only when none of the assigned partitions has data.**

There is no periodic polling or fixed collection window. With a free slot, waiting mainly comes from wake-up and
scheduling; when all slots are occupied, requests must wait for capacity to execute. `BatchSize` limits the number of
requests per handler call: a batch may contain just one request at low traffic or several requests already queued in the
same partition during a burst.

![Opportunistic merging flow](https://raw.githubusercontent.com/eventhorizon-cli/RequestBatcher/main/docs/assets/request-batcher-opportunistic-merging.png)

The diagram shows normal consumption and omits cancellation and shutdown. [BufferQueue's consumer loop](https://github.com/eventhorizon-cli/BufferQueue/blob/90c6208f940592f797ab8cf78ad4af88d1163649/src/BufferQueue/BufferPullConsumer.cs#L59)
checks the notification version before waiting. If new data was signaled during the scan, it retries the pull to avoid a
missed wake-up.

Requests are held in memory and cannot be recovered after a process failure.

> **Batching does not require callers to assemble a collection.** Separate callers can each submit one `TRequest`
> concurrently. RequestBatcher coalesces requests already queued for the same partition into handler batches of up to
> `BatchSize`. `ExecuteAsync(IEnumerable<TRequest>)` is an additional submission option, not a prerequisite for
> batching.

[Full documentation](https://github.com/eventhorizon-cli/RequestBatcher#readme) |
[简体中文](https://github.com/eventhorizon-cli/RequestBatcher/blob/main/README.zh-CN.md)

## Installation and DI Registration

```bash
dotnet add package RequestBatcher
```

The package supports .NET 8 and .NET 10 applications through `net8.0` and `net10.0` assets.

Register the response-enabled handler and batcher with `AddRequestBatcher` at startup. The handler depends on your
application's `IPriceStore` implementation. At the call site, resolve `IRequestBatcher<PriceQuery, PriceQuote?>` from
the same DI container or inject it into an application service's constructor.

```csharp
using Microsoft.Extensions.DependencyInjection;
using RequestBatcher;

// At startup: register the store and response-enabled batcher.
services.AddScoped<IPriceStore, PriceStore>();
services.AddRequestBatcher<PriceQuery, PriceQuote?, PriceQueryHandler>(
    ServiceLifetime.Scoped,
    options =>
    {
        // Four concurrent handler batches, shared by all eight queue partitions.
        options.MaxConcurrency = 4;
        options.BatchSize = 100;
        options.MaxPendingRequests = 5_000;
        // Equal product IDs route to the same partition; separate batches can still run concurrently.
        options.UsePartitionKey(query => query.ProductId, partitionCount: 8);
    });

// At the call site: resolve the typed response interface from the application's service provider.
var batcher = serviceProvider.GetRequiredService<IRequestBatcher<PriceQuery, PriceQuote?>>();
var quote = await batcher.ExecuteAsync(new PriceQuery(ProductId: 101));
```

Use `Microsoft.Extensions.DependencyInjection` and `RequestBatcher`. The [Request/Response Batching](#requestresponse-batching)
section below defines `PriceQuery`, `PriceQuote`, `PriceQueryHandler`, and the `IPriceStore` dependency. In application
services, constructor-inject `IRequestBatcher<PriceQuery, PriceQuote?>` instead of resolving it manually.

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
    Task ExecuteAsync(TRequest request, CancellationToken cancellationToken = default);

    // Submit an existing group; the task waits for every request in that submission.
    Task ExecuteAsync(
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
    Task<TResponse> ExecuteAsync(
        TRequest request,
        CancellationToken cancellationToken = default);

    // Submit a group and return responses in the same order as the input sequence.
    Task<IReadOnlyList<TResponse>> ExecuteAsync(
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
        batcher.ExecuteAsync(request, cancellationToken);
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
        priceBatcher.ExecuteAsync(new PriceQuery(productId), cancellationToken);

    public Task<IReadOnlyList<PriceQuote?>> FindManyAsync(
        IEnumerable<PriceQuery> requests,
        CancellationToken cancellationToken = default) =>
        priceBatcher.ExecuteAsync(requests, cancellationToken);
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

**Deduplication is limited to the current handler batch:** it does not combine entries across handler batches or queue
partitions. The selector overload evaluates the raw request for every entry after the unordered backend result has been
indexed by key, so duplicate entries receive the same response.

All responses are computed before assignment; a factory failure leaves unset entries unchanged. Reference-type results
are shared, not cloned.

If deduplication is unnecessary, remove `Distinct()` and keep the same response factory. Alternatively, assign entries
individually with `entry.SetResponse(response)`, or use the ordered `entries.SetResponses(responses)` overload after producing exactly one response per original entry in input order,
including duplicates and missing requests.

The keyed selector overload is recommended for unordered backend results; the ordered overload must receive the aligned
sequence, never raw backend rows. The explicit-group overload returns responses in input order even when RequestBatcher
splits the requests across handler batches or queue partitions.
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
await batcher.ExecuteAsync(orderWriteRequests, cancellationToken);
```

This overload snapshots the group and submits it as one producer operation, then waits for every request. In `Wait`
mode, an oversized group enters the queue as consecutive capacity-sized slices. In `Fail` mode, the whole group must
fit immediately.

**The submission does not force one handler call:** it may still be split by `BatchSize` or partition routing.

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
| `UsePartitionKey(selector, partitionCount)` | Not set (one partition) | Enables keyed routing with the explicitly configured partition count; `partitionCount` must be greater than one and equal keys use one partition. |

No ordering is guaranteed for handler execution, including when `MaxConcurrency = 1`.

### Partition Count and Concurrency

**Without `UsePartitionKey`, all requests use one partition**, regardless of CPU count or `MaxConcurrency`.
A single partition can still supply several concurrent handler batches. Increase `MaxConcurrency` when you need more
downstream concurrency.

**One partition is the preferred default when keyed routing is not needed.** The existing [partition benchmark](https://github.com/eventhorizon-cli/RequestBatcher/blob/main/docs/evaluations/bufferqueue-2.0.3/README.md#issue-6-partitions)
compared one and eight partitions at `BatchSize = 64` and `MaxConcurrency = 8`. In the unkeyed cases, eight partitions
achieved 0.97–1.01 times the throughput of one partition, showing no consistent advantage.

A preloaded burst of 64 unkeyed requests produced one batch with one partition, versus eight batches with eight
partitions. Keeping one partition avoids this fragmentation and reduces partially filled handler calls while retaining
asynchronous concurrency controlled by `MaxConcurrency`.

When keyed routing is needed, set the partition count explicitly through `UsePartitionKey`. This configuration uses
eight queue partitions sharing four handler execution slots:

```csharp
builder.Services.AddRequestBatcher<ProductQueryItem, ProductBatchHandler>(
    ServiceLifetime.Scoped,
    options =>
    {
        // Per-pod handler-batch limit. Six pods x four slots = at most 24 handler batches cluster-wide.
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

For queries, assume one handler batch makes one downstream batch call and other handler work takes negligible time.
Then estimate concurrency using **downstream batch-call QPS** and mean response time (RT, in seconds):

```text
Average concurrent calls = batch-call QPS × RT
MaxConcurrency_per_pod ≈ ceil(average concurrent calls / pod_count × headroom)
```

Use monitoring data for an existing operation, or expected values and load-test results for a new one. QPS and RT must
refer to the same operation and configuration. Recalibrate with production metrics after launch.

For example, six active pods making a combined 1,920 downstream batch queries per second at a mean RT of 50 ms have
about `1,920 × 0.050 = 96` concurrent calls on average. With even traffic distribution, that is `96 / 6 = 16` per pod.

**16 estimates average load; it is not a recommended concurrency limit by itself.** Allow headroom within downstream
rate limits and connection-pool capacity, then load-test to verify latency and throughput.

### Tuning `BatchSize`, Partitions, and `MaxConcurrency` Together

Continuing the example, the existing calls each query 20 distinct products: `1,920 × 20 = 38,400` request items/s,
with possible duplicates across calls. Try `BatchSize = 80`: suppose a handler receives 80 items and deduplicates them
to 60 products. It queries those products in two parallel calls of 30, awaits both, and assigns results to all 80 items.

![Deduplicate 80 items, split queries, and map responses](https://raw.githubusercontent.com/eventhorizon-cli/RequestBatcher/main/docs/assets/request-batcher-batch-fanout.png)

**Merging lets different submissions share a deduplication opportunity; splitting keeps individual downstream calls
small.** Choose the sizes separately: `BatchSize` limits items before deduplication, while the handler controls products
per downstream call.

Under these assumptions, handlers process `38,400 / 80 = 480` batches/s, making `480 × 2 = 960` downstream calls and
querying `960 × 30 = 28,800` product items/s. That is **50% fewer calls and 25% fewer product items queried**, while
all requests still receive results.

![Call volume, product items queried, and RT before and after merging](https://raw.githubusercontent.com/eventhorizon-cli/RequestBatcher/main/docs/assets/request-batcher-batch-sizing.png)

The diagram assumes downstream mean RT rises from 50 ms to 55 ms. **These are illustrative estimates, not benchmark
results.** Fewer calls and repeated queries are potential benefits; longer RT is a cost. Compare end-to-end latency
and actual resource use to decide whether the tradeoff is worthwhile.

`BatchSize = 80` guarantees neither batches of 80 nor 60 distinct products after deduplication. Split the actual distinct
products: with no duplicates, the handler needs three calls of `30 + 30 + 20`. Only requests currently queued in the same
pod and partition can merge, **without an added wait to fill a batch**. Partition count and consumption speed both
affect merging, so start load testing with fewer partitions.

### Estimating Handler and Downstream Concurrency Separately

**`MaxConcurrency` limits concurrent handler batches, not the downstream calls within each handler.** In this example,
handler-batch QPS is 480. Estimate handler concurrency using the mean duration of the whole handler, including all child
queries, response mapping, and scope disposal, rather than a single downstream call's RT:

```text
MaxConcurrency_per_pod ≈ ceil(handler-batch QPS × mean handler duration / pod_count × headroom)
```

Also check downstream concurrency against the maximum number of calls per handler. For example, with
`MaxConcurrency = 4`, batches of 80 can each split into three parallel queries, allowing `4 × 3 = 12` downstream calls
at once from one batcher. Account for all pods as well.

![Handler concurrency versus downstream call concurrency](https://raw.githubusercontent.com/eventhorizon-cli/RequestBatcher/main/docs/assets/request-batcher-fanout-concurrency.png)

The value 4 illustrates the relationship; it is not a recommendation for the traffic above. To strictly limit downstream
calls, use a shared concurrency limit for child queries in the handler.

### Validating Merging and Deduplication with Load Tests

Use the existing 20-product batch calls as the baseline. Keep request data, arrival rate, duplicate fraction, hot-key
distribution, and downstream resource limits consistent. Cover low, normal, and burst traffic, comparing:

- **Work performed:** actual batch sizes, distinct products per batch, downstream call counts, and total product items
  queried, counting repeated queries across batches.
- **Latency and throughput:** downstream and handler mean RT, caller end-to-end P95/P99, queue wait, throughput, errors,
  and throttling.
- **Resource costs:** application and downstream CPU, memory allocations, and connection-pool use.

![Configure, load-test, measure, and recalculate concurrency](https://raw.githubusercontent.com/eventhorizon-cli/RequestBatcher/main/docs/assets/request-batcher-sizing-feedback.png)

After each adjustment, recalculate concurrency using measured handler-batch QPS and duration, then retest merging.
**Aim to reduce downstream work while meeting latency and resource requirements**; larger batches or lower downstream
QPS alone do not determine the best configuration.

Size `MaxPendingRequests`, the queue capacity, from `items/second per batcher × acceptable queue time`, then verify
admission latency and memory use under bursts.

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

**The queue partition count is not the handler concurrency level.** Partitions organize queueing and routing, while
`MaxConcurrency` sets the global maximum number of concurrent handler batches.

Without a partition key, the queue uses one internal partition to avoid fragmenting unrelated requests. With a finite
integer numeric key or a non-null string key, it uses the explicit count supplied to `UsePartitionKey`, so equal keys
retain locality while different keys can progress independently.

Multiple batches from one partition can execute concurrently, and all partitions share the same execution slots. One
`BatchDispatchLoop` owns them all. It acquires an execution slot before pulling an auto-committed batch, so there is no
application-owned handoff queue behind BufferQueue. Each handler batch releases its slot independently when it finishes,
keeping at most `MaxConcurrency` handler batches in flight. The partition count is fixed at registration and independent
of CPU count.

| Configuration | Routing and dispatch |
| --- | --- |
| No `UsePartitionKey` | All requests use one queue partition; one partition can supply multiple asynchronous handler batches that compete for global execution slots. |
| `UsePartitionKey(selector, partitionCount)` | The partition-key function runs for every item. Equal finite integer numeric keys or non-null string keys use one of the configured partitions; separate handler batches for an equal key can execute concurrently. |

RequestBatcher provides no global, partition-local, or partition-key handler execution ordering guarantee.

Partition routing is optional:

```csharp
services.AddRequestBatcher<PriceQuery, PriceQuote?, PriceQueryHandler>(
    ServiceLifetime.Scoped,
    options =>
    {
        options.BatchSize = 256;
        options.MaxConcurrency = 4;

        // Optional. Without a partition-key function, all requests use one shared partition.
        options.UsePartitionKey(query => query.ProductId, partitionCount: 8);
    });
```

Equal finite, integer-valued numeric keys or equal non-null string keys are routed to one queue partition. A partition
key controls routing only: it does not force related requests into one handler batch, deduplicate them, or serialize
their handler execution.

### Example: Merging Repeated Queries

Several `PriceQuery` requests in one batch may refer to the same `ProductId`. The handler deduplicates product IDs,
issues one batch query, and maps results by `ProductId` to every original request, including duplicates. Response
assignment does not depend on database row order.

Routing by `ProductId` puts equal product IDs in one partition, but does not guarantee the same batch.
**Deduplication applies only within the current handler batch**; separate batches may query the same product again.
It is not a cache across batches.

The runnable [PostgreSQL Web API sample](https://github.com/eventhorizon-cli/RequestBatcher/tree/main/samples/RequestBatcher.Deduplication) demonstrates the complete flow:
`PriceQueryHandler` uses `Distinct()` on product IDs, builds a result dictionary keyed by product ID, and assigns
responses with `entries.SetResponses(request => pricesByProductId.GetValueOrDefault(request.ProductId))`.
Missing products return `null`.

## License

[MIT](https://github.com/eventhorizon-cli/RequestBatcher/blob/main/LICENSE)
