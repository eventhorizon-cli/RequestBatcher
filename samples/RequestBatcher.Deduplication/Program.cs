using Dapper;
using global::RequestBatcher;
using Npgsql;
using RequestBatcher.Deduplication;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("PostgreSql")
    ?? throw new InvalidOperationException("The 'ConnectionStrings:PostgreSql' setting is required.");

builder.Services.AddSingleton<NpgsqlDataSource>(
    _ => new NpgsqlDataSourceBuilder(connectionString).Build());
builder.Services.AddRequestBatcher<PriceUpdate, PriceUpdateHandler>(
    ServiceLifetime.Scoped,
    options =>
    {
        options.BatchSize = 100;
        options.MaxConcurrency = 4;
        options.MaxPendingRequests = 10_000;
        options.FullMode = RequestBatchFullMode.Wait;
        // Optional: equal product IDs share one of eight keyed partitions; omit this line to use one shared partition.
        options.UsePartitionKey(static update => update.ProductId, partitionCount: 8);
    });
// The name selects an independent batcher; requests and responses can be primitive values.
builder.Services.AddRequestBatcher<int, decimal?, PriceQueryHandler>(
    batcherName: "price",
    handlerLifetime: ServiceLifetime.Scoped,
    configure: options =>
    {
        options.BatchSize = 100;
        options.MaxConcurrency = 4;
        options.MaxPendingRequests = 10_000;
        options.FullMode = RequestBatchFullMode.Wait;
        // Optional: equal product IDs share one of eight keyed partitions; omit this line to use one shared partition.
        options.UsePartitionKey(static productId => productId, partitionCount: 8);
    });

var app = builder.Build();
await InitializeDatabaseAsync(app.Services.GetRequiredService<NpgsqlDataSource>());

app.MapPost("/prices", ExecuteAsync);
app.MapGet("/prices/{productId:int}", FindAsync);
app.MapPost("/prices/query", FindManyAsync);

await app.RunAsync();

static async Task<IResult> ExecuteAsync(
    PriceUpdate update,
    IRequestBatcher<PriceUpdate> batcher,
    CancellationToken cancellationToken)
{
    if (update.ProductId <= 0 || update.Version <= 0 || update.Price < 0)
    {
        return Results.BadRequest("ProductId and Version must be positive; Price cannot be negative.");
    }

    await batcher.ExecuteAsync(update, cancellationToken).ConfigureAwait(false);
    return Results.NoContent();
}

// [FromKeyedServices("price")] also works on constructor parameters and MVC Controller Action parameters.
// Constructor DI: https://learn.microsoft.com/en-us/dotnet/core/extensions/dependency-injection/overview#keyed-services
// Action DI: https://learn.microsoft.com/en-us/aspnet/core/mvc/controllers/dependency-injection?view=aspnetcore-8.0#action-injection-with-fromkeyedservices
static async Task<IResult> FindAsync(
    int productId,
    [FromKeyedServices("price")] IRequestBatcher<int, decimal?> batcher,
    CancellationToken cancellationToken)
{
    if (productId <= 0)
    {
        return Results.BadRequest("ProductId must be positive.");
    }

    var result = await batcher.ExecuteAsync(productId, cancellationToken).ConfigureAwait(false);
    return result is null ? Results.NotFound() : Results.Ok(result);
}

static async Task<IResult> FindManyAsync(
    int[]? productIds,
    [FromKeyedServices("price")] IRequestBatcher<int, decimal?> batcher,
    CancellationToken cancellationToken)
{
    if (productIds is null || productIds.Any(static productId => productId <= 0))
    {
        return Results.BadRequest("Product IDs must be positive.");
    }

    // A list may span handler batches; results retain input order, duplicates, and null for missing IDs.
    var results = await batcher.ExecuteAsync(productIds, cancellationToken).ConfigureAwait(false);
    return Results.Ok(results);
}

static async Task InitializeDatabaseAsync(NpgsqlDataSource dataSource)
{
    await using var connection = await dataSource.OpenConnectionAsync().ConfigureAwait(false);
    await connection.ExecuteAsync(
        """
        CREATE TABLE IF NOT EXISTS product_prices
        (
            product_id bigint PRIMARY KEY,
            version bigint NOT NULL,
            price numeric(18, 2) NOT NULL,
            updated_at timestamp with time zone NOT NULL DEFAULT CURRENT_TIMESTAMP
        );
        """).ConfigureAwait(false);
}
