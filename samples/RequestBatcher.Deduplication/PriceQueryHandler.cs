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

        // Resolve every original request, including duplicates, without depending on database row order.
        // Missing products return null. The factory receives TRequest, not its entry wrapper.
        entries.SetResponses(request => pricesByProductId.GetValueOrDefault(request.ProductId));

        // If query deduplication is unnecessary, remove Distinct() above and keep the same response factory.
        // Two other assignment styles are available; use only ONE style for each entry:
        // 1. Assign each entry individually:
        // foreach (var entry in entries)
        //     entry.SetResponse(pricesByProductId.GetValueOrDefault(entry.Request.ProductId));
        // 2. Assign an already aligned response sequence:
        // var responses = entries.Select(entry => pricesByProductId.GetValueOrDefault(entry.Request.ProductId));
        // entries.SetResponses(responses);
        // This overload requires input order and exactly one result per original entry, including duplicates and
        // missing keys. Do not pass database rows directly; map them back to the entries as shown above.
    }
}
