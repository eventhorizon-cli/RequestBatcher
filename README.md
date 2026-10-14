# RequestBatcher

[![NuGet](https://img.shields.io/nuget/v/RequestBatcher.svg)](https://www.nuget.org/packages/RequestBatcher)
[![Build](https://github.com/eventhorizon-cli/RequestBatcher/actions/workflows/dotnet-build.yml/badge.svg)](https://github.com/eventhorizon-cli/RequestBatcher/actions/workflows/dotnet-build.yml)
[![Codecov](https://codecov.io/gh/eventhorizon-cli/RequestBatcher/graph/badge.svg)](https://codecov.io/gh/eventhorizon-cli/RequestBatcher)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

**English** | [简体中文](README.zh-CN.md)

RequestBatcher is an in-process .NET library for turning individual requests into downstream batch operations. Callers
submit one request at a time and await their own result, while RequestBatcher combines requests that are already queued,
limits handler-batch concurrency and queue capacity, and invokes the application handler with a batch. It is intended for
database, cache, and API operations that support processing multiple items together.

![Request coalescing illustration](docs/assets/request-batcher-request-coalescing.png)

Separate concurrent requests can be combined into one batch before a downstream batch operation. The illustration is
an example: only requests already queued in the same partition can be combined into one handler batch.

## How Requests Are Batched: Opportunistic Merging

RequestBatcher uses [BufferQueue](https://github.com/eventhorizon-cli/BufferQueue) to combine queued requests within a
.NET process and pass them to an application handler. Callers obtain `IRequestBatcher<TRequest>` or
`IRequestBatcher<TRequest, TResponse>` through DI, submit requests, and await their individual results. The current
dependency is [BufferQueue 2.0.3](https://www.nuget.org/packages/BufferQueue/2.0.3); applications do not register or
configure it separately.

RequestBatcher uses **Opportunistic Merging**. Writing a request to BufferQueue signals that data is available and wakes
an idle consumer. The dispatch loop acquires a free execution slot, then uses the pull API to retrieve requests already
queued. After dispatching a batch, it continues the loop: if another slot is available, it can pull and dispatch again
without waiting for the previous batch to finish. It waits for the next push notification only when none of the assigned
partitions has data.

There is no periodic polling or fixed collection window. With a free slot, waiting mainly comes from wake-up and
scheduling; when all slots are occupied, requests must wait for capacity to execute. `BatchSize` limits the number of
requests per handler call: a batch may contain just one request at low traffic or several requests already queued in the
same partition during a burst.

![Opportunistic merging flow](docs/assets/request-batcher-opportunistic-merging.png)

The diagram shows normal consumption and omits cancellation and shutdown. [BufferQueue's consumer loop](https://github.com/eventhorizon-cli/BufferQueue/blob/90c6208f940592f797ab8cf78ad4af88d1163649/src/BufferQueue/BufferPullConsumer.cs#L59)
checks the notification version before waiting. If new data was signaled during the scan, it retries the pull to avoid a
missed wake-up.

Requests are held in memory and cannot be recovered after a process failure.

> **Batching does not require callers to assemble a collection.** Separate callers can each submit one `TRequest`
> concurrently. RequestBatcher coalesces requests already queued for the same partition into handler batches of up to
> `BatchSize`. `ProcessAsync(IEnumerable<TRequest>)` is an additional submission option, not a prerequisite for
> batching.

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
var quote = await batcher.ProcessAsync(new PriceQuery(ProductId: 101));
```

Use `Microsoft.Extensions.DependencyInjection` and `RequestBatcher`. The [Request/Response Batching](#requestresponse-batching)
section below defines `PriceQuery`, `PriceQuote`, `PriceQueryHandler`, and the `IPriceStore` dependency. In application
services, constructor-inject `IRequestBatcher<PriceQuery, PriceQuote?>` instead of resolving it manually.

## When to Use It

Use RequestBatcher when requests are independent, temporary in-memory queuing is acceptable, and the downstream
operation benefits from receiving multiple items:

- database writes that can use batch `INSERT`, `UPDATE`, or `UPSERT`;
- cache reads or writes and downstream APIs that already accept multiple items;
- short traffic bursts where internal queue capacity and downstream concurrency must be bounded;
- work where caller cancellation should discard only requests that have not been dispatched, while dispatched work
  must be allowed to finish independently of that caller;
- related requests that benefit from batch-level merging without relying on handler ordering.

For database updates, if partial success within one handler invocation would leave inconsistent state, the handler
should execute that invocation in one transaction. RequestBatcher propagates the handler outcome but cannot roll back
writes that have already been committed.

## When Not to Use It

RequestBatcher is not a durable background queue or a transaction coordinator:

- Accepted work must survive process failure. Use durable storage or a reliable message broker.
- The operation must commit or roll back with the caller's transaction. Keep it in that transaction.
- The downstream operation requires automatic retries or exactly-once effects. RequestBatcher provides neither.
- The handler requires a minimum batch size or a fixed collection window. RequestBatcher dispatches currently queued
  work without waiting to fill a batch.
- An in-flight downstream operation must stop as soon as its individual caller disconnects, times out, or cancels.
  One handler batch can contain requests from several callers, so caller cancellation tokens are not forwarded to the
  handler and cannot cancel the shared handler call.

## APIs

### Request-Only API

Use `IRequestBatcher<TRequest>` when callers only need the handler outcome:

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

### Request/Response API

Use `IRequestBatcher<TRequest, TResponse>` when every accepted request must yield a typed result:

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

RequestBatcher awaits each handler's `ValueTask` once and never returns it to a caller. Synchronously completing
handlers can therefore avoid a `Task` allocation, while asynchronous I/O can use `async ValueTask`.

## Request-Only Batching

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
        options.MaxConcurrency = 4;
        options.BatchSize = 256;
        options.MaxPendingRequests = 5_000;
        options.UsePartitionKey(request => request.OrderId, partitionCount: 8);
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
request; the caller does not need to know which batch or partition contained it.

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
[PostgreSQL Web API sample](samples/RequestBatcher.Deduplication) implements the same lookup flow directly with Dapper and PostgreSQL.

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
The caller's `Task<TResponse>` completes only after the handler succeeds and its item's response has been set.

## Explicit Group Submission

When the caller already has multiple requests, it can submit them with one call:

```csharp
await batcher.ProcessAsync(orderWriteRequests, cancellationToken);
```

RequestBatcher snapshots the sequence and submits it as one producer operation. In `Wait` mode, a group no larger than
`MaxPendingRequests` is admitted atomically; a larger group is admitted as consecutive capacity-sized slices as
capacity becomes available. In `Fail` mode, the whole group must fit immediately. The returned `Task` waits for every
request from the submission. This does not force the sequence into one handler call: it may still be split by
`BatchSize` or partition routing.

> **An explicit group is not a partition boundary.** With more than one partition, RequestBatcher routes every item
> independently; it does not send the whole submission to one partition. The group stays in one partition when no
> partition key is configured or when every item produces the same partition key. `MaxConcurrency = 1` limits handler
> concurrency but does not merge keyed partitions.

## Single vs. Group Submission

In this documentation, a **submission** is one caller invocation of `ProcessAsync`. A **handler batch** is the
`IReadOnlyList<TRequest>` or `IReadOnlyList<RequestBatchItem<TRequest, TResponse>>` passed to one `HandleAsync`
invocation. They are not the same boundary.

| Behavior | Single request | Explicit group |
| --- | --- | --- |
| Input | Uses the supplied `TRequest`. | Enumerates once and snapshots the sequence. An empty sequence completes immediately. |
| Capacity | Reserves capacity for one request. | `Wait` admits an oversized group in consecutive capacity-sized slices; `Fail` requires the whole group to fit immediately. |
| Routing | Routes the request using the configured routing mode. | Routes every item independently, so one submission can span several partitions. |
| Handler calls | May be handled together with other requests already queued in the same partition. | Does not create a handler boundary. Items can be split by partition and `BatchSize`, and may run in parallel. |
| Completion | The returned `Task` represents this request's actual handler outcome. | The returned `Task` waits for every item in the submission. |
| Failure | A handler failure fails every request in that handler batch. | Some items may succeed before another handler batch fails. The group `Task` faults, but successful work is not rolled back. |
| Cancellation | Cancellation removes the request only before dispatch. | The same token applies to every item; undispatched items can be canceled while dispatched items continue to their actual outcome. |

## Request Processing Flow

The internal queue's partition count is not the handler concurrency level. Partitions organize queueing and routing;
the global `MaxConcurrency` setting limits the number of handler batches that can execute at once. Multiple batches
from one partition can execute concurrently, and all partitions share the same execution slots.

Without `UsePartitionKey`, RequestBatcher uses one internal queue partition. This avoids fragmenting unrelated requests
across queues and leaves them together for natural batching. When `UsePartitionKey` is configured with a finite integer
numeric key or a non-null string key and an explicit partition count, the queue uses that many partitions so equal keys
retain locality while different keys can progress independently. `P` is a routing detail; `H = MaxConcurrency` remains
the global handler-batch concurrency limit. A single partition can continuously produce
multiple asynchronous handler batches, so partitions do not impose a one-batch-at-a-time limit.

1. A caller submits one request through `ProcessAsync`.
2. RequestBatcher admits the request according to the configured capacity and routes it to an in-memory partition.
3. The internal `BatchDispatchLoop` acquires a free handler slot, then pulls and auto-commits up to `BatchSize`
   requests that are already queued.
4. The handler outcome completes the `Task` returned to every caller represented in that handler batch.

`BatchSize` is an upper bound, not a minimum. RequestBatcher does not hold the first request for a fixed batching
window, so low traffic may produce single-request batches while concurrent traffic naturally produces larger batches.

![RequestBatcher architecture](docs/assets/request-batcher-architecture.png)

The architecture diagram shows the coordinator, the in-memory partition queue, and the separate `Task` completion
path for each accepted request.

### Dispatch Scheduling

Here, `H = MaxConcurrency` is the maximum number of handler batches running at once. Without a partition key there is
one internal queue partition; with a numeric or string partition key, `P` is the explicit count supplied to
`UsePartitionKey`.

![Internal dispatch scheduling](docs/assets/request-batcher-dispatch-scheduling.png)

One `BatchDispatchLoop` owns all `P` partitions and shares `H` global execution slots. It waits for a free slot before
pulling a queue batch, then starts its handler and returns to waiting for another slot. When all slots are occupied,
requests remain in BufferQueue. As soon as any one handler batch completes, its slot becomes free and the loop can pull
the next batch while the other handlers keep running. It does not wait for all currently running batches to finish.

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

## Partition-Key Routing

A partition key is an optional routing rule for related requests:

```csharp
options.MaxConcurrency = 4;
options.UsePartitionKey(request => request.OrderId, partitionCount: 8);
```

Equal finite, integer-valued numeric keys or equal non-null string keys are routed to the same queue partition among the
configured partition count. The count must be greater than one.
Different keys can still share a partition, so keys and partitions are not one-to-one.

Partition routing does not force all requests for one key into the same handler batch, and it does not deduplicate
requests. It does not provide an execution-order or mutual-exclusion boundary.

### Example: Merging Repeated Updates

Suppose several `PriceUpdate` requests have the same `ProductId`. A handler can group its current batch by product and
write only the highest version. Routing by `ProductId` improves locality, but separate handler calls can still process
that product concurrently.

The storage layer must still prevent stale versions from overwriting newer state across batches because a partition key
does not place every update for one product into the same batch. The runnable
[PostgreSQL Web API sample](samples/RequestBatcher.Deduplication) demonstrates both safeguards:

- the write handler merges repeated product updates within one batch, then performs one bulk upsert;
- the upsert condition ignores older versions that arrive in later batches;
- the query handler deduplicates product IDs before issuing one SQL query for the batch.

## Routing Rules and Dispatch

`MaxConcurrency` is the global maximum number of concurrent handler batches. Without a partition key, the queue has one
internal partition. With a numeric or string partition key, it has the explicit number of partitions supplied to
`UsePartitionKey`, and one `BatchDispatchLoop` pulls from all of them. The count is fixed at registration and is
independent of CPU count.

| Configuration | Request routing | Dispatch behavior |
| --- | --- | --- |
| No `UsePartitionKey` | Every request uses one queue partition, avoiding queue fragmentation for unrelated requests. | Batches use the shared global execution slots; one partition can supply multiple asynchronous batches. |
| `UsePartitionKey(selector, partitionCount)` | A finite integer numeric key or non-null string key routes equal values to one of the configured partitions; different keys can share a partition. | Equal keys can still be dispatched in separate handler batches at the same time. |

RequestBatcher provides no global, partition-local, or partition-key ordering guarantee. A partition key controls only
queue routing; it is not a serialization mechanism.

When keyed routing is enabled, the queue rotates pull opportunities among readable partitions. This does not promise
equal handler execution time or fairness between individual keys. String routing considers the complete key, so a shared
prefix alone does not force different keys into one partition. Exact partition assignments are internal and may change
between versions; equal keys still route together within a batcher instance. Such changes can affect batch composition
and the amount of duplicate work merged inside a handler batch.

A handler runs on the dispatch call path until it completes or reaches an incomplete asynchronous wait. CPU-heavy
or synchronously blocking work before that point can delay other batches even when execution slots remain free.
`MaxConcurrency` is an upper bound; it does not automatically parallelize synchronous handler code.

## Processing Semantics

### Completion and Failure

- A successful handler call completes every caller represented in that handler batch.
- If the handler throws a non-cancellation exception, those callers receive the original exception.
- Response tasks represent a handler `OperationCanceledException` as cancellation. A single response preserves its
  cancellation token and throws `TaskCanceledException` when awaited; the original cancellation exception instance,
  message, and derived type are not preserved.
- RequestBatcher does not retry a failed handler.
- The batch overload waits for the whole submitted group and faults if any handler call involved in that submission
  fails.
- If several handler calls fail for one explicit group, `await` throws one original exception following normal `Task`
  semantics. All distinct exception instances remain available through `Task.Exception.InnerExceptions`; one handler
  exception fanned out to several requests is recorded once.

### Caller Cancellation

Caller cancellation can remove a request only before handler dispatch. Once the handler starts, RequestBatcher reports
the real handler outcome instead of changing the caller's `Task` to canceled. This prevents cancellation from hiding a
side effect that may already have happened.

The token passed to the handler belongs to RequestBatcher's processing lifetime, not to an individual caller. One
caller's cancellation therefore cannot cancel work for the other requests in the same batch.

For an explicit group, the caller token is applied to every item. Cancellation can therefore leave a mix of canceled
items and items that were already dispatched. The returned `Task` still waits for all items; it faults if any item
faults, otherwise it is canceled when at least one item was canceled. Completed side effects are not rolled back.

### Capacity and Backpressure

`MaxPendingRequests` sets the bounded BufferQueue capacity for requests that remain queued. A batch is auto-committed
when the dispatch loop pulls it, before its handler starts. Therefore, up to `MaxConcurrency * BatchSize` pulled
requests can be executing in addition to queued capacity. It is not a limit on the size of an explicit submission or
on the number of callers waiting for capacity:

- `FullMode = Wait` waits asynchronously for enough capacity and honors caller cancellation while waiting.
- In `Wait` mode, a group no larger than the capacity is admitted atomically. A larger group is split into consecutive
  capacity-sized slices. Cancellation can leave already dispatched requests running while undispatched requests are
  canceled.
- `FullMode = Fail` requires the whole submission to fit immediately. Otherwise, including when a group is larger than
  the capacity, it returns a faulted `Task` with `RequestBatchQueueFullException` without admitting any item.

### Shutdown and Disposal

`DisposeAsync` stops accepting new requests, drains every accepted submission (including submissions waiting for
capacity), then releases coordinator-owned resources. Concurrent and repeated disposal calls share the same operation.
When registered through `AddRequestBatcher`, the application container owns async disposal.

## Dependency Injection and Logging

`AddRequestBatcher` registers the handler, coordinator, and internal BufferQueue topic in the application's existing
`IServiceCollection`. The application does not configure BufferQueue directly, and RequestBatcher never builds a
nested service provider.

Scoped and transient handlers are resolved once per handler batch in an asynchronous scope. A singleton handler is
reused across batches and must be thread-safe when `MaxConcurrency > 1`.

Logs use the application's `Microsoft.Extensions.Logging` pipeline under the category
`RequestBatchCoordinator<TRequest>`. Handler failures are logged with their original exception, and request payloads
are not logged.

## Sample Application

- [PostgreSQL Web API sample](samples/RequestBatcher.Deduplication)

## License

RequestBatcher is available under the [MIT License](LICENSE).
