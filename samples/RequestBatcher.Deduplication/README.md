# PostgreSQL Web API Sample

**English** | [简体中文](README.zh-CN.md)

This sample uses [BufferQueue](https://github.com/eventhorizon-cli/BufferQueue) through RequestBatcher. A push wakes the
consumer path, which pulls currently queued requests when an execution slot is available; there is no fixed collection
window. `BatchSize` is only a maximum, while `MaxConcurrency` limits concurrent handler batches per process and
`partitionCount` controls keyed routing.

This is a small RequestBatcher example. Concurrent `POST /prices` calls are collected into handler batches. The write
handler keeps the highest version for each product and writes its batch with one Dapper PostgreSQL upsert.

Concurrent `GET /prices/{productId}` calls are also batched through the named `price` pipeline. The query handler
accepts primitive `int` product IDs and returns `decimal?` prices. It widens distinct IDs to `long[]` for the PostgreSQL
`bigint` column, then distributes each returned price to all matching callers. `POST /prices/query` submits a JSON
`int[]` to the same pipeline and returns nullable prices in input order.
The handler uses LINQ `Distinct()` and then calls
`entries.SetResponses(id => pricesById.GetValueOrDefault(id)?.Price)`.
The factory receives the original request once per entry, including duplicates; database result order does not matter
and missing products return null. Deduplication applies within each handler batch; separate batches may query the same
product again. An explicit list can be split across multiple handler batches, so one `ExecuteAsync` list submission is
not one handler invocation.

If query deduplication is unnecessary, remove `Distinct()` and keep the same response factory. The query handler also
shows two commented assignment alternatives: `entry.SetResponse(response)` for individual entries, or
`entries.SetResponses(responses)` for an already aligned response sequence. Use one assignment approach per entry.
The ordered-sequence overload requires exactly one response per original entry in input order, including duplicates
and missing products; do not pass the database rows directly to it.

`UsePartitionKey(update => update.ProductId, partitionCount: 8)` keeps equal update IDs in one of eight keyed processing
partitions while allowing different IDs to progress independently. The query pipeline uses the same routing idea for its
primitive IDs. Without a partition key, RequestBatcher uses one shared partition. The database only updates a row when
its incoming version is newer, so stale updates are also rejected across handler batches; equal-key requests can still
be handled by separate concurrent batches. Partition routing does not select a named batcher or guarantee handler order.
Updates with the same product ID and version should carry the same price; the sample does not resolve conflicting
prices at an equal version.

The query handler is registered as `AddRequestBatcher<int, decimal?, PriceQueryHandler>(batcherName: "price", ...)`.
The name selects the complete query pipeline, including its handler, capacity, and partitioning. Different names can
register independent pipelines for the same primitive request type, and the write pipeline remains the unnamed,
structured `PriceUpdate` pipeline. The public API can be used like this:

```csharp
using Microsoft.Extensions.DependencyInjection;
using RequestBatcher;

services.AddRequestBatcher<int, decimal?, PriceQueryHandler>(
    batcherName: "price",
    handlerLifetime: ServiceLifetime.Scoped,
    configure: options =>
    {
        options.BatchSize = 100;
        options.MaxConcurrency = 4;
        options.UsePartitionKey(id => id, partitionCount: 8);
    });

var batcher = serviceProvider
    .GetRequiredKeyedService<IRequestBatcher<int, decimal?>>("price");
var prices = await batcher.ExecuteAsync(new[] { 101, 102, 101 });
```

Inject that same named pipeline with `[FromKeyedServices("price")] IRequestBatcher<int, decimal?>` on a constructor,
Controller Action, or Minimal API handler parameter. The [root README](../../README.md#named-batchers-and-primitive-requests)
shows complete constructor and Controller Action examples; this sample uses Minimal API handler parameters in `Program.cs`.
See Microsoft's official documentation for
[constructor injection with keyed services](https://learn.microsoft.com/en-us/dotnet/core/extensions/dependency-injection/overview#keyed-services)
and [Controller Action injection](https://learn.microsoft.com/en-us/aspnet/core/mvc/controllers/dependency-injection?view=aspnetcore-8.0#action-injection-with-fromkeyedservices).
A single GET submits one `int` and returns its price,
or `404` when the price is missing. The batch POST returns one nullable `decimal` per input ID, preserving duplicates
and missing IDs.

## Run

Start PostgreSQL from the repository root:

```bash
docker compose -f samples/RequestBatcher.Deduplication/compose.yaml up -d
```

Run the API:

```bash
dotnet run --project samples/RequestBatcher.Deduplication/RequestBatcher.Deduplication.csproj
```

The API creates the `product_prices` table at startup. RequestBatcher options stay directly in `Program.cs`; the
PostgreSQL connection string and ASP.NET Core logging settings remain in `appsettings.json`.

## Endpoints

Submit one update:

```bash
curl --request POST http://localhost:5080/prices \
  --header "Content-Type: application/json" \
  --data '{"productId":101,"version":3,"price":17.90}'
```

Read the persisted price:

```bash
curl http://localhost:5080/prices/101
```

On a fresh database, the previous update returns `17.90`. A missing product returns `404`:

```bash
curl --include http://localhost:5080/prices/102
```

Read several prices with one batch submission. Results preserve input order, including duplicate and missing IDs:

```bash
curl --request POST http://localhost:5080/prices/query \
  --header "Content-Type: application/json" \
  --data '[101,102,101,999]'
```

With only product `101` seeded, the response is `[17.90,null,17.90,null]`: the duplicate `101` remains duplicated,
and both missing IDs are represented by `null`. Product IDs must be positive. The API returns `204 No Content` for a
successful update and waits for the applicable handler batch before completing each query request.

Database failures propagate to the HTTP request as a server error; RequestBatcher does not retry them automatically.
RequestBatcher is in-process, so pending requests are not recovered after a process failure.

## Observability

For OpenTelemetry in this Web API, install `RequestBatcher` with your OpenTelemetry hosting and exporter
packages, then call `AddRequestBatcherInstrumentation()` from `WithTracing`. `RequestBatcher.Execute` covers the
caller's full wait; each `RequestBatcher.HandleBatch` span is an independent Consumer trace linked to the calls that
entered that batch. See the [observability guide](../../docs/observability-design.md) and
[correlation diagram](../../docs/diagrams/observability.svg).

For optional end-to-end validation of the Compose observability sample, see the [integration test guide](../../tests/it/README.md)
([中文](../../tests/it/README.zh-CN.md)). It reuses the running stack and verifies one Cart `Execute` per HTTP request,
cross-trace batch links, Pricing/database parent-child spans, and metrics with the explicit integration-test opt-in.
