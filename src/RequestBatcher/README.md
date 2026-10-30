# RequestBatcher

[![RequestBatcher](https://img.shields.io/nuget/v/RequestBatcher.svg?label=RequestBatcher)](https://www.nuget.org/packages/RequestBatcher)
[![RequestBatcher.OpenTelemetry](https://img.shields.io/nuget/v/RequestBatcher.OpenTelemetry.svg?label=RequestBatcher.OpenTelemetry)](https://www.nuget.org/packages/RequestBatcher.OpenTelemetry)

[Full documentation, design notes, benchmarks, and samples](https://github.com/eventhorizon-cli/RequestBatcher#readme)

RequestBatcher is an in-process .NET library for submitting individual or explicitly grouped requests to an application
handler. It opportunistically combines requests already queued in the same partition, limits handler-batch concurrency,
and bounds queued capacity. It is useful when a database, cache, or downstream API can process several items together.

## How Requests Are Batched: Opportunistic Merging

![Opportunistic batching animation](https://raw.githubusercontent.com/eventhorizon-cli/RequestBatcher/main/docs/assets/request-batcher-conveyor.gif)

Illustration: one partition, `MaxConcurrency=2`, and `BatchSize=4`. When an execution slot is available, the dispatch
loop pulls queued requests without waiting to fill a batch. See the [animation source](https://github.com/eventhorizon-cli/RequestBatcher/tree/main/docs/animations/opportunistic-merging).

RequestBatcher uses `MaxConcurrency` to limit concurrent Handler batches; its consumption loop pulls and dispatches
batches. See the
[internal dispatch design](https://github.com/eventhorizon-cli/RequestBatcher/blob/main/docs/batch-dispatch-redesign.md#topology).

**RequestBatcher does not wait to fill a batch or add a fixed collection window.** When an execution slot is available,
it dispatches up to `BatchSize` requests already queued in one partition. Requests still queue when all slots are occupied;
normal scheduling and downstream execution also take time. Single-request callers can share a batch, and one explicit
group can span several Handler batches. See the [batching flow](https://github.com/eventhorizon-cli/RequestBatcher#how-requests-are-batched-opportunistic-merging).

**A lower `MaxConcurrency` may let more requests accumulate while waiting for an execution slot.** This is concurrency
queueing, not an added collection delay: **even with one slot, a lone queued request runs as soon as that slot is available.**
See [Configuring Partitions and MaxConcurrency](#configuring-partitions-and-maxconcurrency) for configuration tradeoffs,
and the [low-concurrency throughput estimate and downstream batching example](https://github.com/eventhorizon-cli/RequestBatcher#estimating-handler-and-downstream-concurrency-separately).

Calling `ExecuteAsync` in a loop and awaiting each call before submitting the next gives RequestBatcher no concurrent
work from that caller to merge. Use a bounded number of concurrent submissions when measuring actual handler batch sizes;
concurrency can share a batch, but does not guarantee one batch or a full `BatchSize`.

## When to Use It

**Prefer independent read-only queries**, such as database lookups, cache multi-gets, and
downstream batch APIs. It is also useful for short bursts where queue capacity and downstream concurrency need explicit
limits.

RequestBatcher is not a durable queue, transaction coordinator, retry system, or exactly-once mechanism. Requests are held
in memory and are not recovered after process failure. It does not wait for a minimum batch size or fixed collection
window. A caller cancellation can remove work only before handler dispatch; it cannot cancel a shared handler call after
dispatch. Do not use it when a side effect must remain inside the caller's transaction or must stop immediately when one
caller disconnects.

**Redis workloads are not necessarily a good fit.** StackExchange.Redis already supports pipelining and connection
multiplexing, so batching savings may not offset RequestBatcher's queueing, scheduling, and result dispatch costs.
Poorly chosen `BatchSize`, `MaxConcurrency`, or other settings can also reduce throughput and increase tail latency.
If the caller already has a set of keys, consider using `MGET`/`MSET` directly. Before introducing RequestBatcher to
combine individual requests, compare throughput, P95/P99 latency, and memory allocations against direct Redis client use.

## Installation and DI Registration

```bash
dotnet add package RequestBatcher
```

The package supports .NET 8 and .NET 10.

The current version does not support NativeAOT publishing or trimming.

Two registration styles are available:

- **Handler type**: `AddRequestBatcher` registers a separate Handler class with an explicit `ServiceLifetime`.
- **Delegate Handler**: `AddDelegateRequestBatcher` registers a pipeline whose static Handler delegate is supplied to `ExecuteAsync`; services are injected through its parameters. **Delegate binding is not supported for NativeAOT or trimming.** See [Delegate Handlers](#delegate-handlers).

For primitive request types such as `int` or `string`, prefer [Named RequestBatcher](#named-batchers-and-primitive-requests)
to distinguish operations that share the same request type.

Typed registration supports both completion-only and per-request responses:

```csharp
services.AddRequestBatcher<TRequest, THandler>(ServiceLifetime.Scoped);
services.AddRequestBatcher<TRequest, TResponse, THandler>(ServiceLifetime.Scoped);
```

`RequestBatchCoordinator<TRequest>` is internal. Code that previously referenced it directly must use the public
`IRequestBatcher<TRequest>` abstraction instead.

`AddRequestBatcher` registers the handler, coordinator, and internal queue in the application's existing
`IServiceCollection`. The application does not register or configure BufferQueue separately. Scoped and transient typed
handlers are resolved in an async scope once per handler batch; singleton handlers are reused and must be thread-safe
when `MaxConcurrency > 1`.

Handler execution uses an independent batch context and a DI scope separate from the HTTP request scope; pass needed
business data explicitly in the request. See [Handler Execution Context](#handler-execution-context).

### Quickstart

Create an ASP.NET Core empty project with `dotnet new web`, install the package, and replace `Program.cs` with this
complete example. It uses an in-memory catalog and the default single partition; no database is required.

```csharp
using Microsoft.Extensions.DependencyInjection;
using RequestBatcher;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddRequestBatcher<PriceLookup, decimal?, CatalogHandler>(ServiceLifetime.Singleton);

var app = builder.Build();
app.MapGet("/prices/{productId:long}", async (
    long productId,
    IRequestBatcher<PriceLookup, decimal?> batcher,
    CancellationToken cancellationToken) =>
{
    var price = await batcher.ExecuteAsync(new PriceLookup(productId), cancellationToken);
    return price is { } value ? Results.Ok(value) : Results.NotFound();
});
app.Run();

public sealed record PriceLookup(long ProductId);

public sealed class CatalogHandler : IRequestBatchHandler<PriceLookup, decimal?>
{
    private static readonly Dictionary<long, decimal> Prices = new() { [101] = 17.90m };

    public ValueTask HandleAsync(
        IReadOnlyList<RequestBatchEntry<PriceLookup, decimal?>> entries,
        CancellationToken cancellationToken = default)
    {
        entries.SetResponses(request => Prices.TryGetValue(request.ProductId, out var price) ? price : null);

        // Alternatively, replace the call above with per-entry assignment.
        // foreach (var entry in entries)
        // {
        //     entry.SetResponse(Prices.TryGetValue(entry.Request.ProductId, out var price) ? price : null);
        // }

        return ValueTask.CompletedTask;
    }
}
```

Run `dotnet run --urls http://localhost:5080`, then request `GET http://localhost:5080/prices/101` for `17.90`;
an unknown product returns `404`. **Assign exactly one response to every entry before the Handler completes.**
An explicit `null` is valid; a missing assignment fails the entire Handler batch.

Inject `IRequestBatcher<TRequest>` or `IRequestBatcher<TRequest, TResponse>` into application services. A single call
uses `ExecuteAsync(request)`; an explicit group uses `ExecuteAsync(requests)` and returns results in input order.

For request-only processing, implement `IRequestBatchHandler<TRequest>` with an
`IReadOnlyList<TRequest>` parameter and register it with the first overload. A successful handler call completes every
request in that handler batch.

### Response Assignment APIs

Response handlers assign one response to each `RequestBatchEntry<TRequest, TResponse>`. The following signatures omit implementation details:

```csharp
public sealed class RequestBatchEntry<TRequest, TResponse>
{
    public TRequest Request { get; }
    public void SetResponse(TResponse response);
}

public static class RequestBatchEntryExtensions
{
    public static void SetResponses<TRequest, TResponse>(
        this IReadOnlyList<RequestBatchEntry<TRequest, TResponse>> entries,
        Func<TRequest, TResponse> responseFactory);

    public static void SetResponses<TRequest, TResponse>(
        this IReadOnlyList<RequestBatchEntry<TRequest, TResponse>> entries,
        IEnumerable<TResponse> responses);
}
```

Use one assignment approach per handler batch. `SetResponse` assigns an individual entry. The factory overload evaluates
the factory in entry order and creates every response before assigning any entry. The sequence overload requires exactly
one response per entry and assigns them in entry order. Assigning `null` is valid for nullable response types; every entry
must be assigned exactly once before the handler returns successfully.

### Delegate Handlers

Use `AddDelegateRequestBatcher` for short handling logic supplied to `ExecuteAsync` as a **static delegate**.
Services are injected through parameters and resolved per Handler batch. A pipeline binds one delegate; use a separate
named batcher for a different Handler. Registration has no lifetime argument. Delegate binding requires dynamic code
and does not support NativeAOT or trimming. See the [complete delegate example and parameter rules](https://github.com/eventhorizon-cli/RequestBatcher#delegate-handlers).

### Named Batchers and Primitive Requests

For `int`, `string`, and other shared request types, prefer a distinct batcher name for each business operation.
Names select independent Handlers, queues, capacities, and concurrency limits. Both typed and delegate registrations
accept `batcherName`; callers resolve the matching keyed service. Names are case-sensitive and cannot be empty or
whitespace. See [Named RequestBatcher and keyed injection](https://github.com/eventhorizon-cli/RequestBatcher#named-requestbatcher-instances-and-primitive-requests).

### Partition-Key Routing

Partition-key routing is optional. It keeps equal finite integer-valued numeric keys or equal non-null string keys in the
same queue partition, but it does not deduplicate requests, serialize a key, or force all requests for that key into one
handler batch. Different keys can share a partition. Use a deterministic, side-effect-free selector:

```csharp
options.UsePartitionKey(query => query.ProductId, partitionCount: 4);
```

`partitionCount: 1` behaves like no keyed routing and does not invoke the selector. Counts below one are rejected. Exact
partition assignments are internal and may change between versions.

### Configuring Partitions and MaxConcurrency

Partition count and handler concurrency are independent:

- `partitionCount` controls queue locality and routing;
- `MaxConcurrency` limits concurrent handler batches for the registered batcher across all partitions;
- one partition can supply multiple concurrent handler batches;
- no handler execution ordering is guaranteed, including when `MaxConcurrency = 1`.

**Use one partition by default; add partitions only for a clear keyed-routing need with measured benefits.**
**At low traffic, multiple partitions can split requests that could otherwise be combined, producing more small batches
and increasing handler and downstream calls even with explicit batch submission.**

**Avoid `MaxConcurrency = 1` unless you have carefully evaluated its queueing cost and downstream constraints. Start with `2` for typical workloads.**
Too many slots can produce smaller batches and reduce merging; too few can increase queueing.
**Lower concurrency can improve merging but increase tail latency:** with one slot, a slow Handler batch delays queued
requests even at low traffic, and `Task.WhenAll` holds the slot until the slowest child query completes. Two slots let
a later batch run while one batch is still awaiting I/O. Compare downstream work saved with end-to-end P95/P99;
increase the limit if measured throughput or latency requires it and downstream capacity permits.

Low Handler concurrency can still support substantial traffic: each batch processes multiple requests and can run
multiple downstream calls within a single Handler batch. Under sustained load, item throughput is roughly
`MaxConcurrency × actual mean batch size / mean Handler seconds`; use measured batch sizes and durations.
After deduplication, query directly if the items fit in one downstream call. Otherwise, split with `Chunk`, start each async query, and await
`Task.WhenAll` before assigning responses. Neither `MaxConcurrency` nor `Task.WhenAll` caps those child calls. For this approach, a `BatchSize` of
`3–10` times the desired downstream batch size is a tuning starting point; validate it against downstream capacity.
See [Estimating Handler and Downstream Concurrency Separately](https://github.com/eventhorizon-cli/RequestBatcher#estimating-handler-and-downstream-concurrency-separately).

**Do not evaluate only high-load results; compare performance before and after adopting RequestBatcher under typical traffic too.**
Test typical, peak, and short-burst traffic with matched resources, downstream configuration, request arrival patterns,
item counts, and data distributions. Compare throughput, mean and P95/P99 end-to-end latency, actual batch sizes,
downstream call counts, and resource costs. Typical traffic may offer fewer batching opportunities, so batching savings
may not offset admission and scheduling overhead; requests also queue when no execution slot is available.
High-load throughput gains do not replace this comparison.

Before tuning, collect the existing system's per-instance typical and peak QPS, request-item counts, downstream RT and batch limits,
and latency target; see [Preparing Inputs for an Agent](https://github.com/eventhorizon-cli/RequestBatcher#preparing-inputs-for-an-agent).
These inputs support an initial configuration proposal, not a performance guarantee.

### Configuration Reference

| Option | Default | Behavior |
| --- | ---: | --- |
| `BatchSize` | `128` | Maximum requests passed to one handler invocation; it is not a minimum. |
| `MaxConcurrency` | `1` | Maximum concurrent handler batches across all partitions. Explicitly configure `2` as a typical starting point; retain `1` only after careful evaluation. |
| `MaxPendingRequests` | `8192` | Capacity for requests still queued in memory; pulled requests can already be executing. |
| `FullMode` | `Wait` | `Wait` waits for capacity; an oversized explicit group is admitted in capacity-sized slices. `Fail` rejects the whole group immediately when it cannot fit. |
| `UsePartitionKey(selector, partitionCount)` | Not set (one partition) | Routes equal supported keys together when the count is greater than one; it does not serialize execution. |

## Processing Contract

RequestBatcher forms handler batches opportunistically from requests currently queued in one partition. It does not add a
collection delay. Therefore, a low-traffic batch may contain one request, and one explicit `ExecuteAsync(requests)` call
may be split by `BatchSize` or partition routing. Separate submissions can share a handler batch.

**Even with one partition, an explicit submission can be split into multiple handler batches; it does not guarantee
one handler call.** Each handler batch contains at most `BatchSize` requests available when the queue is pulled,
without preserving caller grouping boundaries.

### Handler Execution Context

A Handler batch can combine requests from multiple callers, and the Handler runs in its own batch execution context.
Do not rely on access to an originating request's `HttpContext` through `IHttpContextAccessor`, or on caller ambient
state such as `AsyncLocal` values, `ILogger.BeginScope` scopes, `Activity.Current`, or `Baggage`. The Handler's DI scope
is also separate from the HTTP request scope: a scoped dependency does not reuse the originating request-scoped
instance or state initialized there.

Read values the Handler needs before submission, such as currency, language, or a business correlation ID, and carry
them as immutable request data. Do not pass an `HttpContext`, request service provider, or request-scoped service
instance to preserve caller context. RequestBatcher tracing connects `Execute` and `Handle` spans with links; those
links do not restore caller ambient context. Activities created inside a Handler still use the Handler batch span as
their parent.

### Completion, Cancellation, and Draining

- A successful handler call completes every request in that handler batch.
- A non-cancellation handler exception fails every request in that handler batch with that exception.
- A handler `OperationCanceledException` produces canceled response tasks. RequestBatcher does not retry failed handlers.
- A group task waits for every item; some items may succeed before another handler batch fails, and successful side effects
  are not rolled back. If several handler batches fail, `Task.Exception.InnerExceptions` retains each distinct exception
  instance, while one handler exception fanned out to multiple requests is recorded once.
- Caller cancellation removes work only before dispatch. After dispatch, the caller observes the actual handler outcome.
  The token passed to the handler belongs to RequestBatcher's processing lifetime, not to one caller. For an explicit group,
  cancellation can leave a mix of canceled undispatched items and dispatched items that report their actual outcome.
  Its task waits for all items, faults if any item faults, and otherwise is canceled if any item was canceled.
- In `FullMode.Fail`, a submission that cannot fit immediately, including one larger than `MaxPendingRequests`, fails with
  `RequestBatchQueueFullException` and admits no item. `MaxPendingRequests` is not a limit on explicit group size or callers
  waiting for capacity.
- For a response handler, assign exactly one response to every `RequestBatchEntry` before returning successfully. An unset
  entry causes `InvalidOperationException` and fails that handler batch. Explicitly assigning `null` is valid for nullable
  responses. `SetResponses` with a factory evaluates all responses before assigning any entry.
- Explicit group responses preserve input order and duplicates even when the group spans handler batches or partitions.
- Queue capacity excludes executing requests: up to `MaxConcurrency * BatchSize` pulled requests may be in flight in addition.
- `DisposeAsync` stops new admissions, drains accepted work including submissions waiting for capacity, and releases owned
  resources. Concurrent and repeated disposal calls share one operation. When registered through DI, the application
  container owns async disposal. When handlers need DI scopes, stop producers and await outstanding calls before
  disposing the container; pending batches cannot create new scopes once container disposal begins.

The default is `FullMode = Wait`. `MaxPendingRequests` bounds queued requests, not callers or submissions waiting for
capacity, and is not a process-wide memory limit; apply an ingress concurrency or rate limit when total waiting work must
be bounded. See the root documentation's [Capacity and Backpressure](https://github.com/eventhorizon-cli/RequestBatcher#capacity-and-backpressure)
section for the HTTP admission example. A caller cancellation timer can cancel capacity waits and undispatched requests;
it does not impose a deadline on `ExecuteAsync` after dispatch.

Caller cancellation cannot abort a shared Handler after dispatch, even when all callers cancel; configure downstream
timeouts independently. The downstream operation must support cancellation or its own timeout mechanism. See
[Caller Cancellation](https://github.com/eventhorizon-cli/RequestBatcher#caller-cancellation) for an example. Disposal drains
accepted work before stopping the consumer, so a stalled downstream call can keep `DisposeAsync` waiting.

## Observability

The core package exposes the `RequestBatcher` `ActivitySource` and `Meter` through built-in .NET APIs and has no
OpenTelemetry dependency. Install the optional
[`RequestBatcher.OpenTelemetry`](https://www.nuget.org/packages/RequestBatcher.OpenTelemetry) package for registration
extensions and coordinated sampling between submission (`Execute`) and handler-batch (`Handle`) spans:

```bash
dotnet add package RequestBatcher.OpenTelemetry
```

Register the extension in the application's existing providers, alongside the hosting and exporter packages used by the
application:

```csharp
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing.AddRequestBatcherInstrumentation())
    .WithMetrics(metrics => metrics.AddRequestBatcherInstrumentation());
```

Without the integration package, register `AddSource("RequestBatcher")` and/or `AddMeter("RequestBatcher")` directly.
The application's sampler then decides `Handle` sampling independently, which may omit the Handler span even when
an `Execute` span was sampled. The integration package samples `Handle` if any corresponding `Execute` span is sampled,
and does not sample it if none are. Metrics-only consumers can use `AddMeter("RequestBatcher")` without the integration
package, or a `MeterListener` that calls `EnableMeasurementEvents` for the desired instruments. Built-in logs use the
application logging pipeline with category prefix `RequestBatcher.RequestBatchCoordinator`; set the `RequestBatcher`
log-level prefix to configure the whole library.

The Meter exposes four histograms: `requestbatcher.submission.size` and `requestbatcher.batch.size` are `Histogram<long>`
in `{request}`; `requestbatcher.handle.duration` and `requestbatcher.execute.duration` are `Histogram<double>` in `ms`.
`requestbatcher.backlog` is an `ObservableGauge<long>` in `{request}` for approximate enqueued items not yet pulled,
excluding capacity waits and active Handler work.
Core supplies size-histogram bucket advice from the largest `BatchSize` registered in the same service collection.
Direct `AddMeter` and the integration extension use the same advice; custom `AddView` settings override it.
Histogram `sum / count` gives the mean. For metric semantics, tags, queries, and bucket rules, see the
[RequestBatcher Metrics guide](https://github.com/eventhorizon-cli/RequestBatcher/blob/main/docs/metrics.md).

See the [full observability documentation](https://github.com/eventhorizon-cli/RequestBatcher#observability) for span names,
links, sampling details, and exporters.

## Samples and Upgrade Notes

When upgrading code that used `RequestBatchItem<TRequest, TResponse>`, rename it to
`RequestBatchEntry<TRequest, TResponse>` and rename `RequestBatchItemExtensions` to
`RequestBatchEntryExtensions`. This is a source and binary breaking change; response assignment behavior is unchanged.

- [Typed-handler PostgreSQL sample](https://github.com/eventhorizon-cli/RequestBatcher/tree/main/samples/RequestBatcher.Deduplication)
- [Delegate-handler PostgreSQL sample](https://github.com/eventhorizon-cli/RequestBatcher/tree/main/samples/RequestBatcher.Delegates)
- [Observability sample](https://github.com/eventhorizon-cli/RequestBatcher/tree/main/samples/RequestBatcher.Observability)
- [Upgrade notes and complete API documentation](https://github.com/eventhorizon-cli/RequestBatcher#readme)

## License

RequestBatcher is released under the [MIT License](https://github.com/eventhorizon-cli/RequestBatcher/blob/main/LICENSE).
