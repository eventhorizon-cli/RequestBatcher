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
        IReadOnlyList<RequestBatchItem<PriceQuery, PriceUpdate?>> items,
        CancellationToken cancellationToken = default)
    {
        // Duplicate callers share one database lookup for their product ID.
        var productIds = items
            .Select(item => item.Request.ProductId)
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
        items.SetResponses(items.Select(item => pricesByProductId.GetValueOrDefault(item.Request.ProductId)));
    }
}
