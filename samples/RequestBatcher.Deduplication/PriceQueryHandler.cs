using Dapper;
using Npgsql;

namespace RequestBatcher.Deduplication;

internal sealed class PriceQueryHandler(NpgsqlDataSource dataSource)
    : IRequestBatchHandler<int, decimal?>
{
    private const string QuerySql = """
        SELECT product_id AS "ProductId",
               version AS "Version",
               price AS "Price"
        FROM product_prices
        WHERE product_id = ANY(@ProductIds);
        """;

    public async ValueTask HandleAsync(
        IReadOnlyList<RequestBatchEntry<int, decimal?>> entries,
        CancellationToken cancellationToken = default)
    {
        // Duplicate callers in this handler batch share one database lookup for their product ID.
        // Widen the public int IDs to match the database's bigint column.
        var productIds = entries
            .Select(entry => (long)entry.Request)
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
        var pricesById = prices.ToDictionary(price => price.ProductId);

        // Resolve every original request, including duplicates, without depending on database row order.
        // Missing products return null. The factory receives TRequest, not its entry wrapper.
        entries.SetResponses(id => pricesById.GetValueOrDefault(id)?.Price);

        // If query deduplication is unnecessary, remove Distinct() above and keep the same response factory.
        // Two other assignment styles are available; use only ONE style for each entry:
        // 1. Assign each entry individually:
        // foreach (var entry in entries)
        //     entry.SetResponse(pricesById.GetValueOrDefault(entry.Request)?.Price);
        // 2. Assign an already aligned response sequence:
        // var responses = entries.Select(entry => pricesById.GetValueOrDefault(entry.Request)?.Price);
        // entries.SetResponses(responses);
        // This overload requires input order and exactly one result per original entry, including duplicates and
        // missing keys. Do not pass database rows directly; map them back to the entries as shown above.
    }
}
