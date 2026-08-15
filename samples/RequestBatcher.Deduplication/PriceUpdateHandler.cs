using Dapper;
using Npgsql;

namespace RequestBatcher.Deduplication;

internal sealed class PriceUpdateHandler(NpgsqlDataSource dataSource)
    : IRequestBatchHandler<PriceUpdate>
{
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

    public async ValueTask HandleAsync(
        IReadOnlyList<PriceUpdate> requests,
        CancellationToken cancellationToken = default)
    {
        // Partition routing serializes equal keys across batches; this merges duplicates inside the current batch.
        var latestUpdates = requests
            .GroupBy(request => request.ProductId)
            .Select(group => group.MaxBy(request => request.Version)!)
            .ToArray();

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
}
