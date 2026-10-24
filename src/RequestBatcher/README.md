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
[BufferQueue 2.1.0](https://www.nuget.org/packages/BufferQueue/2.1.0); applications do not register or configure it
separately.

This is **opportunistic batching**: writing a request to BufferQueue sends a push notification that wakes an idle
consumer. It first acquires one execution slot, then pulls up to `BatchSize` requests (default: 128) from one partition
and dispatches that batch. If fewer requests are available, it takes those immediately without waiting to fill the batch.

Callers obtain `IRequestBatcher<TRequest>` or `IRequestBatcher<TRequest, TResponse>` through DI, submit requests, and
await their individual results.

After dispatching a batch, the loop continues. If another slot is available, it can pull and dispatch the next batch
without waiting for the previous batch to finish. Each batch holds one slot until its processing completes.

**It waits for the next push notification only when none of the assigned partitions has data.**

There is no periodic polling or fixed collection window. With a free slot, waiting mainly comes from wake-up and
scheduling; when all slots are occupied, requests must wait for capacity to execute. `BatchSize` limits the number of
requests per handler call: a batch may contain just one request at low traffic or several requests already queued in the
same partition during a burst.

![Opportunistic merging flow](https://raw.githubusercontent.com/eventhorizon-cli/RequestBatcher/main/docs/assets/request-batcher-opportunistic-merging.png)

The diagram shows normal consumption and omits cancellation and shutdown. [BufferQueue's consumer loop](https://github.com/eventhorizon-cli/BufferQueue/blob/c276547046223c1d62a13e0537b013622c775266/src/BufferQueue/BufferPullConsumer.cs)
checks the notification version before waiting. If new data was signaled during the scan, it retries the pull to avoid a
missed wake-up.

Requests are held in memory and cannot be recovered after a process failure.

> **Batching does not require callers to assemble a collection.** Separate callers can each submit one `TRequest`
> concurrently. RequestBatcher coalesces requests already queued for the same partition into handler batches of up to
> `BatchSize`. `ExecuteAsync(IEnumerable<TRequest>)` is an additional submission option, not a prerequisite for
> batching.

[Full documentation](https://github.com/eventhorizon-cli/RequestBatcher#readme) |
[简体中文](https://github.com/eventhorizon-cli/RequestBatcher/blob/main/README.zh-CN.md)

## When to Use It

**Independent read-only queries are the recommended starting point**: database lookups, cache reads, and downstream
batch-query APIs. They can often share a batch or deduplicate equivalent queries without introducing write side effects.
Requests must still be independent, temporary in-memory queuing must be acceptable, and the downstream operation
must benefit from receiving multiple items:

- database queries that can fetch multiple IDs together, cache multi-gets, and downstream batch reads;
- short traffic bursts where internal queue capacity and downstream concurrency must be bounded;
- work where caller cancellation should discard only requests that have not been dispatched, while dispatched work
  must be allowed to finish independently of that caller;
- related requests that benefit from batch-level merging without relying on handler ordering.

Batch `INSERT`, `UPDATE`, `UPSERT`, and cache writes are also possible, but the application must handle retry
idempotency, concurrent writes across batches, and transaction boundaries. Even equal partition keys do not serialize
handler execution. If partial success within one handler invocation would leave inconsistent state, execute that
invocation in one transaction. A single submission can span several handler batches, so a transaction per handler
does not make the entire submission atomic. RequestBatcher propagates the handler outcome but cannot roll back
writes that have already been committed or recover pending work after a process failure.

## When Not to Use It

Do not use RequestBatcher when work must survive process failure, remain inside the caller's transaction, wait for a
minimum batch size, or rely on automatic retries or exactly-once effects. It is also not
suitable when an in-flight downstream operation must stop as soon as its individual caller disconnects, times out, or
cancels. One handler batch can contain requests from several callers, so caller cancellation tokens are not forwarded
to the handler and cannot cancel the shared handler call.

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

## APIs

The response wrapper is `RequestBatchEntry<TRequest, TResponse>`. When upgrading code that used
`RequestBatchItem<TRequest, TResponse>`, update that type and its extension class name to `RequestBatchEntry`
and `RequestBatchEntryExtensions`, then rebuild all consumers. This is a source and binary breaking change;
response assignment and completion behavior are unchanged.

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
        IReadOnlyList<RequestBatchEntry<TRequest, TResponse>> entries,
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
        IReadOnlyList<RequestBatchEntry<PriceQuery, PriceQuote?>> entries,
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
[PostgreSQL Web API sample](https://github.com/eventhorizon-cli/RequestBatcher/tree/main/samples/RequestBatcher.Deduplication) uses Dapper and PostgreSQL with a named
`IRequestBatcher<int, decimal?>`: callers submit primitive IDs and receive price values.

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

## Named Batchers and Primitive Requests

A request can be a primitive ID. Use the `batcherName` overload when several operations accept the same request type:
for example, `"price"` and `"stock"` can each register an independent `int` pipeline. Each name has its own handler,
options, queue capacity, and concurrency limit. Named and unnamed registrations may coexist.

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RequestBatcher;

services.AddRequestBatcher<int, decimal?, PriceByIdHandler>(
    batcherName: "price",
    handlerLifetime: ServiceLifetime.Scoped,
    configure: options =>
    {
        options.BatchSize = 100;
        options.MaxConcurrency = 4;
        options.UsePartitionKey(id => id, partitionCount: 8);
    });

var batcher = serviceProvider.GetRequiredKeyedService<IRequestBatcher<int, decimal?>>("price");
var prices = await batcher.ExecuteAsync(new[] { 101, 102, 101 });
var options = serviceProvider.GetRequiredService<IOptionsMonitor<RequestBatchOptions<int>>>().Get("price");
```

The handler below uses the `IPriceStore` contract from the request/response example. Register its implementation
in the application's service collection as shown above:

```csharp
public sealed class PriceByIdHandler(IPriceStore store) : IRequestBatchHandler<int, decimal?>
{
    public async ValueTask HandleAsync(
        IReadOnlyList<RequestBatchEntry<int, decimal?>> entries,
        CancellationToken cancellationToken = default)
    {
        var ids = entries.Select(entry => (long)entry.Request).Distinct().ToArray();
        var prices = await store.FindAsync(ids, cancellationToken).ConfigureAwait(false);
        var pricesById = prices.ToDictionary(price => price.ProductId);
        entries.SetResponses(id => pricesById.GetValueOrDefault(id)?.Price);
    }
}
```

Results follow input order, including duplicates; missing products return null. An explicit list can split into
multiple handler batches.

In application services, inject the named batcher through the constructor. Register `PriceLookup` with
`services.AddScoped<PriceLookup>()`; the attribute selects the same `"price"` name used during batcher registration:

```csharp
using Microsoft.Extensions.DependencyInjection;
using RequestBatcher;

public sealed class PriceLookup(
    [FromKeyedServices("price")] IRequestBatcher<int, decimal?> batcher)
{
    public Task<IReadOnlyList<decimal?>> FindAsync(
        IEnumerable<int> productIds,
        CancellationToken cancellationToken = default) =>
        batcher.ExecuteAsync(productIds, cancellationToken);
}
```

In ASP.NET Core MVC, the same attribute can inject the batcher directly into a Controller Action parameter:

```csharp
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using RequestBatcher;

[ApiController]
[Route("api/prices")]
public sealed class PricesController : ControllerBase
{
    [HttpPost("query")]
    public Task<IReadOnlyList<decimal?>> FindAsync(
        [FromBody] int[] productIds,
        [FromKeyedServices("price")] IRequestBatcher<int, decimal?> batcher,
        CancellationToken cancellationToken) =>
        batcher.ExecuteAsync(productIds, cancellationToken);
}
```

Register MVC with `builder.Services.AddControllers()` and map it with `app.MapControllers()`. Minimal API handler
parameters support the same attribute, as shown in the runnable sample. See Microsoft's official documentation for
[keyed services and constructor injection](https://learn.microsoft.com/en-us/dotnet/core/extensions/dependency-injection/overview#keyed-services)
and [Controller Action injection](https://learn.microsoft.com/en-us/aspnet/core/mvc/controllers/dependency-injection?view=aspnetcore-8.0#action-injection-with-fromkeyedservices).

The request-only overload also accepts `batcherName`. Names are case-sensitive and must not be null, empty, or
whitespace; a request type and name identify one pipeline, regardless of response type. Use the original overload
for the unnamed default. Configuration is captured at registration; named options expose that configuration and
do not provide runtime queue reconfiguration. `UsePartitionKey` routes requests within the selected batcher and
does not select a batcher or serialize its handler batches.

The [named versus unnamed benchmark](https://github.com/eventhorizon-cli/RequestBatcher/blob/main/docs/evaluations/named-batching/README.md) keeps request/response types,
handlers and batching options identical, comparing single and explicit submissions with singleton and scoped handlers.

The runnable [PostgreSQL sample](https://github.com/eventhorizon-cli/RequestBatcher/tree/main/samples/RequestBatcher.Deduplication) uses a named `int` price-query batcher with
`decimal?` responses, widens IDs for its PostgreSQL `bigint` column, and accepts a JSON `int[]` at `POST /prices/query`.

## Delegate Handlers

Delegate handlers keep short batch-processing logic next to the `ExecuteAsync` call that submits the work, so you
can read and update the submission and handling code together. They suit straightforward batch lookups or result
mapping. At the call site, use a static lambda, static anonymous method, static local function, or static method
group. When the logic grows or needs to be reused and tested independently, use a separate `IRequestBatchHandler`
class.

**Delegate handlers are optimized for repeated execution**: signature inspection and expression-tree compilation of a
strongly typed invoker happen once on first binding. Subsequent submissions use a reference-equality fast path, and
dispatched batches reuse the cached invoker without reflection, `DynamicInvoke` or argument arrays. This automatic
binding mode requires dynamic code and is not supported for NativeAOT or trimming.

In-memory benchmarks using the same configuration show individual-submission means for delegate and typed handlers
differing by about 1%, with similar allocations and no clear steady-state regression. These measurements exclude the
initial compilation cost; see the [delegate-handler performance comparison](https://github.com/eventhorizon-cli/RequestBatcher/blob/main/docs/evaluations/delegate-handlers/README.md).

Use `AddDelegateRequestBatcher` to declare the request/response contract and options, then supply a static handler
target at the call site:

**Handlers must use static implementations without capturing locals or instance state.** Lambdas, anonymous
methods and local functions must explicitly declare `static`; method groups must refer to ordinary or generic
static methods. Receive services through injected parameters. Delegates may be passed directly or through variables,
fields, parameters, properties or factories, including explicit construction, parentheses and casts.

```csharp
using Microsoft.Extensions.DependencyInjection;
using RequestBatcher;

services.AddKeyedScoped<IPriceStore, PriceStore>("catalog");
services.AddDelegateRequestBatcher<int, decimal?>(
    batcherName: "price",
    configure: options =>
    {
        options.BatchSize = 100;
        options.MaxConcurrency = 4;
    });

var batcher = serviceProvider.GetRequiredKeyedService<IRequestBatcher<int, decimal?>>("price");
var prices = await batcher.ExecuteAsync(
    new[] { 101, 102, 101 },
    static async ValueTask (
        IReadOnlyList<RequestBatchEntry<int, decimal?>> entries,
        [FromKeyedServices("catalog")] IPriceStore store,
        CancellationToken batchCancellation) =>
    {
        var ids = entries.Select(entry => (long)entry.Request).Distinct().ToArray();
        var rows = await store.FindAsync(ids, batchCancellation).ConfigureAwait(false);
        var pricesById = rows.ToDictionary(row => row.ProductId);
        entries.SetResponses(id => pricesById.GetValueOrDefault(id)?.Price);
    });
```

Alternatively, replace the lambda above with a static method or static local function. Choose one form per pipeline:

```csharp
var price = await batcher.ExecuteAsync(101, HandlePricesAsync);

static async ValueTask HandlePricesAsync(
    IReadOnlyList<RequestBatchEntry<int, decimal?>> entries,
    [FromKeyedServices("catalog")] IPriceStore store,
    CancellationToken batchCancellation)
{
    var ids = entries.Select(entry => (long)entry.Request).Distinct();
    var rows = await store.FindAsync(ids, batchCancellation).ConfigureAwait(false);
    var pricesById = rows.ToDictionary(row => row.ProductId);
    entries.SetResponses(id => pricesById.GetValueOrDefault(id)?.Price);
}
```

This uses the `IPriceStore` contract from the request/response example above; register your own implementation
(shown as `PriceStore`). Request-only pipelines use `AddDelegateRequestBatcher<TRequest>()` with a first lambda
parameter of `IReadOnlyList<TRequest>`. Both modes support individual requests and explicit lists through the
`ExecuteAsync` extension methods; the original interfaces have no added members.

For simple or shared request types such as `int` or `string`, prefer a distinct `batcherName` for each
business operation. For example, name a price `int` pipeline `"price"` when another operation also uses `int`,
to avoid delegate collisions or accidental binding. Omit `batcherName` to use the default pipeline. A request type
and name can use only one registration mode: delegate registrations require the handler on every submission, while
typed-handler registrations use the original overloads.

The first valid call binds one delegate to that pipeline. Repeated calls with an equal delegate reuse it; a
different delegate fails before enqueueing. Different handler targets represent different handlers even when their
bodies look identical, so use different batcher names for different operations. Concurrent first calls bind atomically.
Services are supplied per dispatched batch, not captured from the caller's scope.

The first parameter must be the exact batch list type. Other parameters are required DI services, with
`[FromKeyedServices(key)]` selecting a keyed service. The key must be explicit; inherited keys are unsupported.
Web applications may write `[FromServices]` on ordinary
service parameters; it is optional because services are injected by convention. The handler may omit the batch
lifecycle `CancellationToken`; if present, it must declare exactly one and must not give it a default such as
`= default`. This injected token is shared by the batch and is distinct from the optional caller
`ExecuteAsync` `cancellationToken`. The lambda may return `void`,
`Task` or `ValueTask`; `async void` and result-bearing return types are rejected. Every response entry must still
receive a response. List results preserve order and duplicates even when submission spans multiple handler batches.

The delegate handler and its bound delegate are always singleton per pipeline; registration has no
`ServiceLifetime` argument. Injected services retain their registered lifetimes: singleton instances are shared by
the container, scoped instances are shared within one batch, and transient instances are resolved separately for
each parameter. Resolved services are never cached in the singleton handler. Typed-handler registrations continue
to honor their explicit handler lifetime.

Each batch that injects services owns an async DI scope, and callers complete after scope disposal. A delegate
without service parameters skips scope creation.

**The built-in analyzer provides basic compile-time checks.** Both diagnostics are errors by default:

- `RB0001` rejects known non-static lambdas, local functions and instance methods.
- `RB0002` checks known delegate signatures, including batch-list and return types, parameter modifiers and defaults,
  and the restrictions on `CancellationToken` and `[FromKeyedServices]`.

Delegate targets may be unknown after passing through parameters, properties or factories. These forms remain
allowed; callers must ensure the eventual target is static. Runtime binding validates the actual signature, service
binding and delegate identity, but cannot reliably distinguish static-lambda source from a non-capturing ordinary
lambda. Ordinary typed-handler calls are unaffected, and consumers can disable the analyzers. See the
[delegate-handler design](https://github.com/eventhorizon-cli/RequestBatcher/blob/main/docs/delegate-handlers-design.md).

The standalone [delegate PostgreSQL sample](https://github.com/eventhorizon-cli/RequestBatcher/tree/main/samples/RequestBatcher.Delegates) uses delegate handlers for both price updates and
`POST /prices/query` list lookups. Each static lambda injects a keyed scoped `PriceStore` per handler batch.
The [typed-handler sample](https://github.com/eventhorizon-cli/RequestBatcher/tree/main/samples/RequestBatcher.Deduplication) provides the original handler-class examples separately.

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

## Configuration

| Option | Default | Behavior |
| --- | ---: | --- |
| `BatchSize` | `128` | Maximum requests passed to one handler call. |
| `MaxConcurrency` | `1` | Maximum concurrent handler batches. It is independent of the keyed partition count. |
| `MaxPendingRequests` | `8192` | Internal BufferQueue capacity for requests that have not yet been pulled for execution. |
| `FullMode` | `Wait` | Waits for capacity; `Fail` rejects immediately when capacity is unavailable. |
| `UsePartitionKey(selector, partitionCount)` | Not set (one partition) | Requires `partitionCount >= 1`; `1` disables keyed routing without invoking the selector, and larger counts route equal keys to one partition. |

No ordering is guaranteed for handler execution, including when `MaxConcurrency = 1`.

### Partition Count and Concurrency

**Without `UsePartitionKey`, or with `partitionCount: 1`, all requests use one partition**, regardless of CPU count or
`MaxConcurrency`. Setting the count to `1` clears any previously configured key and skips the selector. Counts below `1`
throw `ArgumentOutOfRangeException`; the selector must still be non-null.
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
integer numeric key or a non-null string key and a count greater than one, it uses the count supplied to
`UsePartitionKey`, so equal keys retain locality while different keys can progress independently.

Multiple batches from one partition can execute concurrently, and all partitions share the same execution slots. One
`BatchDispatchLoop` owns them all. It acquires an execution slot before pulling an auto-committed batch, so there is no
application-owned handoff queue behind BufferQueue. Each handler batch releases its slot independently when it finishes,
keeping at most `MaxConcurrency` handler batches in flight. The partition count is fixed at registration and independent
of CPU count.

| Configuration | Routing and dispatch |
| --- | --- |
| No `UsePartitionKey`, or `partitionCount: 1` | All requests use one queue partition; one partition can supply multiple asynchronous handler batches that compete for global execution slots. |
| `UsePartitionKey(selector, partitionCount)` with count > 1 | The partition-key function runs for every item. Equal finite integer numeric keys or non-null string keys use one of the configured partitions; separate handler batches for an equal key can execute concurrently. |

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

The runnable [PostgreSQL Web API sample](https://github.com/eventhorizon-cli/RequestBatcher/tree/main/samples/RequestBatcher.Deduplication) uses primitive `int` IDs in its named
`"price"` pipeline. Its `PriceQueryHandler` deduplicates IDs with `Distinct()`, queries PostgreSQL, and maps rows by
product ID with `entries.SetResponses(id => pricesById.GetValueOrDefault(id)?.Price)`. Callers receive `decimal?`
prices, with `null` for missing products. Both single-ID and list submissions use this handler; list results retain
input order and duplicates even when requests span several handler batches.

## Observability

### Execution Context and Tracing

Background dispatch and consumer monitoring run without inheriting the initializing caller's execution context.
Handlers do not inherit HTTP request activities (`Activity.Current`), logging scopes, or other `AsyncLocal` values
from initialization or submission. The caller's context is preserved after resolution and after awaiting completion.
Pass required application data, such as tenant identifiers, explicitly in requests. Activities created inside a
handler flow normally across its awaits. Singleton constructors must not capture request-specific state; they follow
the container's normal creation rules.

### OpenTelemetry Integration

The core [`RequestBatcher`](https://www.nuget.org/packages/RequestBatcher) package uses built-in .NET `ActivitySource` and
`Meter` APIs and has no OpenTelemetry package dependency. Install the optional
[`RequestBatcher.OpenTelemetry`](https://www.nuget.org/packages/RequestBatcher.OpenTelemetry) package for the provider-builder
registration extensions.
It targets net8.0/net10.0, declares a minimum compatible `RequestBatcher` version, and requires OpenTelemetry 1.20.1
or later. The two packages are versioned independently. The optional package does not create a provider or select an exporter.

Install the OpenTelemetry hosting and exporter packages in the application, then register both providers:

```bash
dotnet add package RequestBatcher
dotnet add package RequestBatcher.OpenTelemetry
dotnet add package OpenTelemetry.Extensions.Hosting
dotnet add package OpenTelemetry.Instrumentation.AspNetCore
dotnet add package OpenTelemetry.Exporter.OpenTelemetryProtocol
```

```csharp
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

services.AddOpenTelemetry()
    .WithTracing(tracing => tracing
        .AddAspNetCoreInstrumentation()
        .AddRequestBatcherInstrumentation()
        .AddOtlpExporter())
    .WithMetrics(metrics => metrics
        .AddRequestBatcherInstrumentation()
        .AddOtlpExporter());
```

Installing [`RequestBatcher.OpenTelemetry`](https://www.nuget.org/packages/RequestBatcher.OpenTelemetry) alone does not register tracing. Add `.AddRequestBatcherInstrumentation()` to the tracing
provider to register the `RequestBatcher` source and decorate the application's sampler with linked-batch sampling.
The extension always samples a `Handle` root when any creation-time `Handle-to-Execute` link context has the `Recorded`
flag and returns `Drop` when every participating `Execute` context is unsampled; other Activities, including `Execute`
and handler child spans, still use the application's sampler. It decorates the sampler selected by code or environment
configuration, regardless of whether `SetSampler` appears before or after the extension.
For direct source registration without this linked-batch policy, use `AddSource("RequestBatcher")` instead;
the independent `Handle` root then follows the application's sampler, so a sampled `Execute` may still have an
unsampled batch and downstream children, leaving its Handle trace incomplete. See the
[RequestBatcher.OpenTelemetry README](https://github.com/eventhorizon-cli/RequestBatcher/blob/main/src/RequestBatcher.OpenTelemetry/README.md)
for migration and linked-batch sampling details.

When created, `RequestBatcher.Execute` is an `Internal` span under the caller's current span; its sampling decision is
made by the application's sampler. It measures the complete caller-visible duration. With an active listener, each
non-empty batch creates an independent `RequestBatcher.Handle` `Consumer` root. When at least one participating
`Execute` context is sampled, the `Handle` span links to every distinct valid participating `Execute` context that
entered processing, including unsampled contexts. Handler database and HTTP spans are children of that batch span and
still use the application's sampler; ParentBased sampling follows the batch's flag, while other samplers may decide
differently. An `Execute` span links only to `Handle` spans that actually handled its requests, and only when it is
sampled and requests all Activity data. If no Execute Activity was created, a valid caller context can provide that
submission's context for batch correlation. These links are added late and do not affect `Execute` sampling; the
application's parent span is not modified. Follow the `Execute`-to-`Handle` links when they are available.
Both spans use the public request type's simple name for `requestbatcher.request.type` (for example, `Int32`),
without namespaces or internal response wrappers. Span queries and samplers must match these short names.

With direct `AddSource("RequestBatcher")` registration, sampling is controlled by the application's tracing provider for
`Execute`, `Handle`, and handler children. A 0% sampler can still create propagation Activities,
so it does not have the same overhead as leaving the RequestBatcher source unregistered. A dropped or propagation-only
Execute retains a valid context that can join the `Handle-to-Execute` links when another Execute in the batch
is sampled. RecordOnly requests all Activity data without the sampled flag and does not affect the `Handle` sampling
decision. With direct source registration, an all-unsampled batch may still be recorded independently; the integration
extension drops it when all participating Execute contexts are unsampled. A mixed batch retains every distinct valid context. Logs, task completion,
cancellation, and handler execution are unchanged. Fixed call metadata is reused; span duration and task completion
semantics are unchanged.
The [20 ms sampling-aware link comparison](https://github.com/eventhorizon-cli/RequestBatcher/blob/main/docs/evaluations/tracing-io-overhead/sampled-links.md)
records low-sampling allocation savings and full-sampling cost; it does not establish a latency improvement.

Named registrations append the public name after a space, for example `RequestBatcher.Execute pricing-queries` and
`RequestBatcher.Handle pricing-queries`; both spans also include `requestbatcher.name`. Unnamed registrations
keep the base names and omit the tag. Links are causal references across the queue, not shared parent edges, trace IDs,
or ordering guarantees. Handler execution does not inherit the caller's execution context; request scopes, baggage,
and other `AsyncLocal` values must be passed explicitly when needed.

For compatibility, custom span-name queries should use `RequestBatcher.Handle` and append a named-registration suffix
after a space, such as `RequestBatcher.Handle pricing-queries`. The `RequestBatcher` ActivitySource and Meter names
are stable. Historical backend spans retain their previous names until backend retention removes them.

![RequestBatcher observability model](https://raw.githubusercontent.com/eventhorizon-cli/RequestBatcher/main/docs/diagrams/observability.svg)

#### Sampling and Context Propagation

The following flow assumes that the optional integration package is installed and
`.AddRequestBatcherInstrumentation()` is registered on the tracing provider. Installing the package alone does not
register tracing. The application-configured OpenTelemetry SDK still makes sampling decisions for each Activity from its
parent context and creation-time tags or links.

![RequestBatcher sampling and context flow](https://raw.githubusercontent.com/eventhorizon-cli/RequestBatcher/main/docs/diagrams/tracing-sampling.svg)

`Execute` uses the caller's current HTTP or business Activity as its parent. Each `Handle` supplies no parent and is
an independent Consumer root; it is the parent of the handler's database and HTTP child spans. If at least one
participating `Execute` context is sampled, that `Handle` links to every distinct submission's `Execute` context.
These links associate handling with submissions without creating parent edges. If all participating contexts are
unsampled, the enabled policy drops the independent `Handle` root. The SDK applies this policy to the
`Handle-to-Execute` link contexts supplied when `Handle` starts.
HTTP instrumentation propagates the batch trace downstream through `traceparent`.
An `Execute`-to-`Handle` link is added at completion only when the `Execute` Activity is sampled and requests all
data; it cannot affect the earlier `Execute` sampling decision. With the integration extension registered, a `Handle` root is
sampled when any creation-time `Handle-to-Execute` link context is sampled and dropped otherwise; this link policy overrides the
independent root decision that a `ParentBased` sampler would otherwise make.

For the common `ParentBasedSampler` policy, a sampled parent produces a sampled child, an unsampled parent drops the child,
and no parent delegates to the configured root sampler. For example, `ParentBasedSampler(new AlwaysOnSampler())` samples
root Activities. A ratio root sampler only applies when there is no parent. With the integration extension registered, the
`Handle` root is the explicit exception: its root decision comes from the creation-time `Handle-to-Execute`
link contexts above,
not from the `ParentBased` root sampler. Handler children then follow the batch's sampled flag under ParentBased.

The observability sample explicitly uses `AlwaysOnSampler`. Separately, the tracing benchmark uses
`ParentBasedSampler(new TraceIdRatioBasedSampler(ratio))` with direct source registration and no linked-batch policy
to measure different sampling rates. These examples do not define the sampling diagram's application sampler.

With the integration extension registered, a 0% ParentBased root ratio can still record `Execute` under a sampled caller parent;
when that sampled submission enters a batch, the linked policy samples the independent `Handle` root.
Dropped spans may retain propagation Activities and correlation/completion state;
0% sampling is not the same as leaving the source unregistered. If a registered listener creates no Execute Activity,
an available caller context can still provide that submission's context when the batch has a sampled submission context.
With no RequestBatcher listener, tracing is bypassed.

When a batch Activity requests all data after sampling, it records `requestbatcher.batch.submission.count`, the number
of distinct public `ExecuteAsync` submissions represented by requests that entered that handler invocation, not the
number of spans. Multiple items from one explicit submission count once per batch; separate submissions count separately
even under the same caller span or when no Execute Activity was created. Canceled queued items do not contribute. The
attribute is computed after sampling and is a span attribute only, not a metric dimension.

`MaxConcurrency` limits active handler batches, not outstanding Execute spans. For example, 1,024 individually submitted
requests can each have an Execute Activity while only eight handler batches run. Activity lifetime covers each complete
submission, so the concurrency setting does not provide a reusable pool of eight tracing objects.

#### Metrics

The matching metrics extension registers the `RequestBatcher` meter. If only metrics are needed, you can choose either
direct core registration or the optional package's metrics extension:

```csharp
services.AddOpenTelemetry()
    .WithMetrics(metrics => metrics.AddMeter("RequestBatcher"));
```

Both options are supported: you can choose direct `AddMeter("RequestBatcher")` registration without installing the
optional package, or install [`RequestBatcher.OpenTelemetry`](https://www.nuget.org/packages/RequestBatcher.OpenTelemetry)
and use the metrics extension. The meter records accepted requests, processed requests
and batches, batch size, and batch duration. Dimensions contain only request type and configured batcher name; payloads,
tenant values, and partition keys are excluded. Request types use their short public name (for example, `Int32`), without
namespaces or internal response wrappers. Identically named types share a dimension; use batcher names to distinguish
pipelines when needed, and update request-type label filters to use short names. Metrics are emitted when a meter
provider listens to the meter.

Instrument names already include `requestbatcher.`, for example `requestbatcher.batches.processed`. The sample exports
`requestbatcher_batches_processed_total` to Prometheus. An additional exporter namespace/prefix of `requestbatcher`
would duplicate that prefix, so leave it unset.

## Sample Application

The [continuous-read observability sample](https://github.com/eventhorizon-cli/RequestBatcher/blob/main/samples/RequestBatcher.Observability/README.md) starts the Cart and Pricing Web APIs, a C# traffic generator, PostgreSQL,
and Grafana OTel LGTM with Docker Compose. It shows complete call spans, independent linked batch spans,
and downstream database spans while concurrent Cart requests submit 2–10 item carts that share RequestBatcher batches.

The [PostgreSQL Web API sample](https://github.com/eventhorizon-cli/RequestBatcher/tree/main/samples/RequestBatcher.Deduplication) demonstrates two pipelines in a Minimal API:

- `POST /prices` submits a structured `PriceUpdate` to the unnamed write batcher. The handler merges updates by
  product ID, keeps the highest version in its batch, and performs a PostgreSQL upsert.
- `GET /prices/{productId}` submits one `int` ID to the named `"price"` batcher and returns a price value, or `404`
  when the product is missing.
- `POST /prices/query` submits a JSON `int[]` to the same named batcher and returns a `decimal?` array in input order,
  preserving duplicates and returning `null` for missing products.

Both query endpoints inject `[FromKeyedServices("price")] IRequestBatcher<int, decimal?>`. With product `101` stored
at `17.90` and products `102` and `999` absent, submitting `[101,102,101,999]` returns `[17.90,null,17.90,null]`.
The list can span multiple handler batches; duplicate IDs share a database lookup only within each handler batch.
See the [sample README](https://github.com/eventhorizon-cli/RequestBatcher/blob/main/samples/RequestBatcher.Deduplication/README.md) for Docker startup and complete curl commands.

The standalone [delegate-handler PostgreSQL sample](https://github.com/eventhorizon-cli/RequestBatcher/tree/main/samples/RequestBatcher.Delegates) uses `AddDelegateRequestBatcher` and static lambdas
for both `POST /prices` updates and `POST /prices/query` list lookups. The query pipeline is named `"price"` and accepts
primitive `int` IDs with `decimal?` responses. Each lambda receives a keyed scoped `PriceStore` from its batch scope;
the delegate handler is singleton and registration has no lifetime argument. The original sample remains the
handler-class version. The delegate sample has its own HTTP and database ports; see its
[README](https://github.com/eventhorizon-cli/RequestBatcher/blob/main/samples/RequestBatcher.Delegates/README.md) for startup and curl commands.

## License

[MIT](https://github.com/eventhorizon-cli/RequestBatcher/blob/main/LICENSE)
