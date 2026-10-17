using global::RequestBatcher;
using Npgsql;
using RequestBatcher.Delegates;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("PostgreSql")
    ?? throw new InvalidOperationException("The 'ConnectionStrings:PostgreSql' setting is required.");

builder.Services.AddSingleton<NpgsqlDataSource>(
    _ => new NpgsqlDataSourceBuilder(connectionString).Build());
builder.Services.AddKeyedScoped<PriceStore>("catalog");

// Delegate forwarding handlers are always singleton; their batch-scoped services are resolved per actual batch.
builder.Services.AddDelegateRequestBatcher<PriceUpdate>(
    configure: options =>
    {
        options.BatchSize = 100;
        options.MaxConcurrency = 4;
        options.MaxPendingRequests = 10_000;
        options.FullMode = RequestBatchFullMode.Wait;
        // Optional: equal product IDs share one of eight keyed partitions; routing does not serialize execution.
        options.UsePartitionKey(static update => update.ProductId, partitionCount: 8);
    });
builder.Services.AddDelegateRequestBatcher<int, decimal?>(
    batcherName: "price",
    configure: options =>
    {
        options.BatchSize = 100;
        options.MaxConcurrency = 4;
        options.MaxPendingRequests = 10_000;
        options.FullMode = RequestBatchFullMode.Wait;
        // Optional: equal IDs route consistently; separate batches can still execute concurrently.
        options.UsePartitionKey(static productId => productId, partitionCount: 8);
    });

var app = builder.Build();
await using (var startupScope = app.Services.CreateAsyncScope())
{
    await startupScope.ServiceProvider
        .GetRequiredKeyedService<PriceStore>("catalog")
        .EnsureSchemaAsync();
}

app.MapPost("/prices", WriteAsync);
app.MapPost("/prices/query", QueryAsync);

await app.RunAsync();

static async Task<IResult> WriteAsync(
    PriceUpdate update,
    IRequestBatcher<PriceUpdate> batcher,
    CancellationToken cancellationToken)
{
    if (update.ProductId <= 0 || update.Version <= 0 || update.Price < 0)
    {
        return Results.BadRequest("ProductId and Version must be positive; Price cannot be negative.");
    }

    await batcher.ExecuteAsync(
        update,
        static async ValueTask (
            IReadOnlyList<PriceUpdate> requests,
            [FromKeyedServices("catalog")] PriceStore store,
            CancellationToken batchCancellationToken) =>
        {
            // Keep the newest update per product in this batch; the SQL version predicate covers overlapping batches.
            var latestUpdates = requests
                .GroupBy(request => request.ProductId)
                .Select(group => group.MaxBy(request => request.Version)!)
                .ToArray();
            await store.WriteLatestAsync(latestUpdates, batchCancellationToken).ConfigureAwait(false);
        },
        cancellationToken).ConfigureAwait(false);
    return Results.NoContent();
}

// FromKeyedServices also selects named batchers in constructors and MVC Controller Actions.
// https://learn.microsoft.com/en-us/dotnet/core/extensions/dependency-injection/overview#keyed-services
// https://learn.microsoft.com/en-us/aspnet/core/mvc/controllers/dependency-injection?view=aspnetcore-8.0#action-injection-with-fromkeyedservices
static async Task<IResult> QueryAsync(
    int[]? productIds,
    [FromKeyedServices("price")] IRequestBatcher<int, decimal?> batcher,
    CancellationToken cancellationToken)
{
    if (productIds is null || productIds.Any(static productId => productId <= 0))
    {
        return Results.BadRequest("Product IDs must be positive.");
    }

    // This direct static lambda call site is reused for every submission to the named price pipeline.
    // Ordinary service parameters can optionally use [FromServices]; keyed services require an explicit key.
    // The handler may omit CancellationToken entirely; if declared, do not give it a default value.
    // It receives the batch lifecycle token; the final ExecuteAsync argument is the caller's optional token.
    var results = await batcher.ExecuteAsync(
        productIds,
        static async ValueTask (
            IReadOnlyList<RequestBatchEntry<int, decimal?>> entries,
            [FromKeyedServices("catalog")] PriceStore store,
            CancellationToken batchCancellationToken) =>
        {
            // Deduplicate only this handler batch, widen IDs for PostgreSQL bigint, then restore original order.
            var distinctProductIds = entries
                .Select(entry => (long)entry.Request)
                .Distinct()
                .ToArray();
            var pricesById = await store
                .FindAsync(distinctProductIds, batchCancellationToken)
                .ConfigureAwait(false);
            entries.SetResponses(id => pricesById.GetValueOrDefault(id)?.Price);
        },
        cancellationToken).ConfigureAwait(false);
    return Results.Ok(results);
}
