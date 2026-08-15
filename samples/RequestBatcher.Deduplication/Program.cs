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
        ConfigureBatching(options);
        // Optional: equal product IDs share a partition; omit this line for round-robin routing.
        options.UsePartitionKey(static update => update.ProductId);
    });
builder.Services.AddRequestBatcher<PriceQuery, PriceQueryHandler>(
    ServiceLifetime.Scoped,
    options =>
    {
        ConfigureBatching(options);
        // Optional: equal product IDs share a partition; omit this line for round-robin routing.
        options.UsePartitionKey(static query => query.ProductId);
    });

var app = builder.Build();
await InitializeDatabaseAsync(app.Services.GetRequiredService<NpgsqlDataSource>());

app.MapPost("/prices", ProcessAsync);
app.MapGet("/prices/{productId:long}", FindAsync);

await app.RunAsync();

static void ConfigureBatching<TRequest>(RequestBatchOptions<TRequest> options)
{
    // RequestBatcher options stay in code so the sample's batching behavior is visible at registration.
    options.BatchSize = 100;
    options.MaxConcurrency = 4;
    options.MaxPendingRequests = 10_000;
    options.FullMode = RequestBatchFullMode.Wait;
}

static async Task<IResult> ProcessAsync(
    PriceUpdate update,
    IRequestBatcher<PriceUpdate> batcher,
    CancellationToken cancellationToken)
{
    if (update.ProductId <= 0 || update.Version <= 0 || update.Price < 0)
    {
        return Results.BadRequest("ProductId and Version must be positive; Price cannot be negative.");
    }

    await batcher.ProcessAsync(update, cancellationToken).ConfigureAwait(false);
    return Results.NoContent();
}

static async Task<IResult> FindAsync(
    long productId,
    IRequestBatcher<PriceQuery> batcher,
    CancellationToken cancellationToken)
{
    if (productId <= 0)
    {
        return Results.BadRequest("ProductId must be positive.");
    }

    var query = new PriceQuery(productId);
    await batcher.ProcessAsync(query, cancellationToken).ConfigureAwait(false);
    return query.Result is null ? Results.NotFound() : Results.Ok(query.Result);
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
