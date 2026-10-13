using Dapper;
using Npgsql;

namespace RequestBatcher.Deduplication;

internal sealed class PriceQueryHandler(NpgsqlDataSource dataSource)
    : IRequestBatchHandler<PriceQuery, PriceUpdate?>
{
    private const string QuerySql = """
        SELECT product_id AS "ProductId",
               version AS "Version",
               price AS "Price"
        FROM product_prices
        WHERE product_id = ANY(@ProductIds);
        """;

    public async ValueTask HandleAsync(
        IReadOnlyList<RequestBatchItem<PriceQuery, PriceUpdate?>> entries,
        CancellationToken cancellationToken = default)
    {
        // Duplicate callers in this handler batch share one database lookup for their product ID.
        var productIds = entries
            .Select(entry => entry.Request.ProductId)
            .Distinct()
            .ToArray();

        await using var connection = await dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        var prices = await connection.QueryAsync<PriceUpdate>(
            new CommandDefinition(
                QuerySql,
                new { ProductIds = productIds },
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        var pricesByProductId = prices.ToDictionary(price => price.ProductId);

        // Publish results only after the query succeeds, so a failed batch cannot expose partial results.
        entries.SetResponses(entries.Select(entry => pricesByProductId.GetValueOrDefault(entry.Request.ProductId)));
    }
}
