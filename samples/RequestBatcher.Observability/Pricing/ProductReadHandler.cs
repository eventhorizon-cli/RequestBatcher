using Npgsql;

namespace RequestBatcher.Observability.Pricing;

internal sealed class ProductReadHandler(NpgsqlDataSource dataSource, ILogger<ProductReadHandler> logger)
    : IRequestBatchHandler<int, Product?>
{
    public async ValueTask HandleAsync(
        IReadOnlyList<RequestBatchEntry<int, Product?>> entries,
        CancellationToken cancellationToken = default)
    {
        var ids = entries.Select(entry => entry.Request).Distinct().ToArray();
        await using var command = dataSource.CreateCommand(
            "SELECT id, name, price FROM products WHERE id = ANY($1)");
        command.Parameters.AddWithValue(ids);
        var products = new Dictionary<int, Product>();
        // Npgsql's query span is a child of the batch span, even when entries came from different HTTP traces.
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var product = new Product(reader.GetInt32(0), reader.GetString(1), reader.GetDecimal(2));
                products.Add(product.Id, product);
            }
        }

        foreach (var entry in entries)
        {
            entry.SetResponse(products.GetValueOrDefault(entry.Request));
        }

        logger.LogInformation("Read {ProductCount} products for {RequestCount} requests in one batch",
            products.Count, entries.Count);
    }
}
