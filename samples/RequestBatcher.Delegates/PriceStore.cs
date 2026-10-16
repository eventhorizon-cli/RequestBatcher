using Dapper;
using Npgsql;

namespace RequestBatcher.Delegates;

internal sealed class PriceStore(NpgsqlDataSource dataSource)
{
    private const string CreateTableSql = """
        CREATE TABLE IF NOT EXISTS product_prices
        (
            product_id bigint PRIMARY KEY,
            version bigint NOT NULL,
            price numeric(18, 2) NOT NULL,
            updated_at timestamp with time zone NOT NULL DEFAULT CURRENT_TIMESTAMP
        );
        """;

    private const string UpsertLatestSql = """
        INSERT INTO product_prices (product_id, version, price)
        SELECT product_id, version, price
        FROM unnest(@ProductIds, @Versions, @Prices) AS rows(product_id, version, price)
        ON CONFLICT (product_id) DO UPDATE
        SET version = EXCLUDED.version,
            price = EXCLUDED.price,
            updated_at = CURRENT_TIMESTAMP
        WHERE product_prices.version < EXCLUDED.version;
        """;

    private const string QuerySql = """
        SELECT product_id AS "ProductId",
               version AS "Version",
               price AS "Price"
        FROM product_prices
        WHERE product_id = ANY(@ProductIds);
        """;

    public async Task EnsureSchemaAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(CreateTableSql, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    public async Task WriteLatestAsync(
        IReadOnlyList<PriceUpdate> latestUpdates,
        CancellationToken cancellationToken = default)
    {
        // The caller supplies at most one update per product; SQL rejects stale versions across batches.
        await using var connection = await dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await connection.ExecuteAsync(
            new CommandDefinition(
                UpsertLatestSql,
                new
                {
                    ProductIds = latestUpdates.Select(update => update.ProductId).ToArray(),
                    Versions = latestUpdates.Select(update => update.Version).ToArray(),
                    Prices = latestUpdates.Select(update => update.Price).ToArray(),
                },
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<IReadOnlyDictionary<long, PriceUpdate>> FindAsync(
        IReadOnlyList<long> productIds,
        CancellationToken cancellationToken = default)
    {
        if (productIds.Count == 0)
        {
            return new Dictionary<long, PriceUpdate>();
        }

        await using var connection = await dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        var prices = await connection.QueryAsync<PriceUpdate>(
            new CommandDefinition(
                QuerySql,
                new { ProductIds = productIds.ToArray() },
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        return prices.ToDictionary(price => price.ProductId);
    }
}
