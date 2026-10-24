# Delegate Handler PostgreSQL Sample

**English** | [简体中文](README.zh-CN.md)

Delegate handlers let you keep short batch-processing logic next to the code that submits requests, so both can be
read and updated together. This sample puts batch deduplication and response mapping in static lambdas at the
endpoints, with database operations in `PriceStore`. For more complex logic that needs reuse or independent tests,
use a separate `IRequestBatchHandler` class, as shown in the [typed-handler sample](../RequestBatcher.Deduplication).

This sample uses RequestBatcher with two delegate pipelines and two HTTP endpoints:

- `POST /prices` writes structured `PriceUpdate` requests through the unnamed request-only pipeline.
- `POST /prices/query` reads primitive `int` product IDs through the named `price` response pipeline and returns
  nullable prices in input order.

This sample names the primitive `int` pipeline `"price"` because simple request types may be reused by different
business operations. Use a distinct `batcherName` for each such operation; omitting it selects the default pipeline.

Both pipelines use the same in-process batching settings. `BatchSize` is a maximum, so an explicit list can be split
across multiple handler batches. Deduplication applies only within the current handler batch. Partition routing keeps
equal keys in the same partition but does not serialize separate batches or guarantee handler order.

## Registration

The delegate handler is always a singleton and has no handler lifetime option. The keyed `PriceStore` is
resolved in a new DI scope for each actual handler batch. The scope is disposed before that batch completes and is
independent of the HTTP request scope. `PriceStore` owns schema creation, writes, and reads; the database datasource is
the application singleton.

```csharp
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using RequestBatcher;
using RequestBatcher.Delegates;

services.AddSingleton<NpgsqlDataSource>(
    _ => new NpgsqlDataSourceBuilder(connectionString).Build());
services.AddKeyedScoped<PriceStore>("catalog");

services.AddDelegateRequestBatcher<PriceUpdate>(options =>
{
    options.BatchSize = 100;
    options.MaxConcurrency = 4;
    options.MaxPendingRequests = 10_000;
    options.FullMode = RequestBatchFullMode.Wait;
    options.UsePartitionKey(update => update.ProductId, partitionCount: 8);
});
services.AddDelegateRequestBatcher<int, decimal?>(
    batcherName: "price",
    configure: options =>
    {
        options.BatchSize = 100;
        options.MaxConcurrency = 4;
        options.MaxPendingRequests = 10_000;
        options.FullMode = RequestBatchFullMode.Wait;
        options.UsePartitionKey(id => id, partitionCount: 8);
    });
```

**Handlers must use static implementations without capturing locals or instance state.** Explicitly declare lambdas,
anonymous methods and local functions `static`, or use ordinary or generic static method groups. Delegates can be
passed through variables, fields, parameters, properties or factories; explicit construction and casts are supported.
Receive services through injected parameters. The built-in analyzer checks known targets and signatures as described below.

The write endpoint passes a direct static lambda to `ExecuteAsync`:

```csharp
await batcher.ExecuteAsync(
    update,
    static async ValueTask (
        IReadOnlyList<PriceUpdate> requests,
        [FromKeyedServices("catalog")] PriceStore store,
        CancellationToken batchCancellationToken) =>
    {
        var latestUpdates = requests
            .GroupBy(request => request.ProductId)
            .Select(group => group.MaxBy(request => request.Version)!)
            .ToArray();
        await store.WriteLatestAsync(latestUpdates, batchCancellationToken);
    },
    cancellationToken);
```

The query endpoint uses one direct static lambda call site for every submission to the named pipeline. It widens the
distinct IDs to `long[]` for PostgreSQL `bigint`, then maps results through every original entry so duplicates and
missing products retain their positions:

```csharp
var prices = await batcher.ExecuteAsync(
    productIds,
    static async ValueTask (
        IReadOnlyList<RequestBatchEntry<int, decimal?>> entries,
        [FromKeyedServices("catalog")] PriceStore store,
        CancellationToken batchCancellationToken) =>
    {
        var ids = entries.Select(entry => (long)entry.Request).Distinct().ToArray();
        var pricesById = await store.FindAsync(ids, batchCancellationToken);
        entries.SetResponses(id => pricesById.GetValueOrDefault(id)?.Price);
    },
    cancellationToken);
```

The first valid call binds the delegate for that pipeline. Later calls with an equal delegate reuse it; supplying
a different delegate fails before admission. Keep one equivalent delegate per pipeline, or use separate batcher
names for different operations.

**The built-in analyzer provides basic compile-time checks.** `RB0001` rejects known non-static targets; `RB0002`
checks known batch and return types, parameter modifiers/defaults, and cancellation/keyed-service restrictions.
Delegates from parameters, properties, factories or fields remain allowed when their target cannot be determined;
callers must ensure that target is static. Runtime binding validates the actual signature, service binding and
delegate identity, but cannot reliably distinguish static-lambda source from a non-capturing ordinary lambda.

The handler may omit the batch-lifecycle `CancellationToken`; if present, it must declare exactly one without a default
such as `= default`. This injected token is distinct from the optional caller `ExecuteAsync` `cancellationToken`.
See the [delegate-handler design](../../docs/delegate-handlers-design.md).

`[FromServices]` is optional for ordinary DI services under the normal conventions. `[FromKeyedServices]` supports
constructor and ASP.NET Core Controller Action injection as well; this sample uses it on Minimal API parameters and on
delegate service parameters. See Microsoft's [keyed service documentation](https://learn.microsoft.com/en-us/dotnet/core/extensions/dependency-injection/overview#keyed-services)
and [Controller Action injection documentation](https://learn.microsoft.com/en-us/aspnet/core/mvc/controllers/dependency-injection?view=aspnetcore-8.0#action-injection-with-fromkeyedservices).

## Run

Start this sample's isolated PostgreSQL instance from the repository root:

```bash
docker compose -f samples/RequestBatcher.Delegates/compose.yaml up -d
```

Run the API:

```bash
dotnet run --project samples/RequestBatcher.Delegates/RequestBatcher.Delegates.csproj
```

The API listens on `http://localhost:5081` and creates the `product_prices` table at startup. The database uses host
port `54330` and database `requestbatcher_delegates`, so it can run beside the other PostgreSQL sample.

Seed a price:

```bash
curl --request POST http://localhost:5081/prices \
  --header "Content-Type: application/json" \
  --data '{"productId":101,"version":3,"price":17.90}'
```

Query several prices:

```bash
curl --request POST http://localhost:5081/prices/query \
  --header "Content-Type: application/json" \
  --data '[101,102,101,999]'
```

With only product `101` seeded, the response is `[17.90,null,17.90,null]`. Duplicate IDs remain duplicated and
missing IDs produce `null`. Product IDs, versions, and prices are validated on writes; query IDs must be positive.
The write endpoint returns `204 No Content` after its handler batch completes.

## Boundaries

The highest version per product is selected inside each write handler batch, and the PostgreSQL version predicate
protects correctness when equal-key batches overlap. The query lambda deduplicates each current batch only; a list
submission is not guaranteed to be one handler invocation and separate batches may query the same product again.
Updates with the same product ID and version should carry the same price; conflicting values at an equal version
are not resolved by this sample.

RequestBatcher is in-process and does not recover accepted requests after a process failure. Database errors propagate
to the HTTP request; the sample does not add automatic retries.

## Observability

The sample can use the `RequestBatcher` package with the application's OpenTelemetry hosting
and exporter packages. Register it with `WithTracing(tracing => tracing.AddRequestBatcherInstrumentation())`.
`RequestBatcher.Execute` measures the full caller wait, while `RequestBatcher.Handle` is an independent Consumer
span linked to contributing calls. See the [observability guide](../../docs/observability-design.md).

For optional end-to-end validation of the Compose observability sample, see the [integration test guide](../../tests/it/README.md)
([中文](../../tests/it/README.zh-CN.md)). It reuses the running stack and verifies one Cart `Execute` per HTTP request,
cross-trace batch links, Pricing/database parent-child spans, and metrics with the explicit integration-test opt-in.
