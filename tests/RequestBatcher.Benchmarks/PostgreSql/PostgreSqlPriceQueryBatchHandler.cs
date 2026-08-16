using Npgsql;
using NpgsqlTypes;
using RequestBatcher.Benchmarks.Infrastructure;

namespace RequestBatcher.Benchmarks.PostgreSql;

internal sealed class PostgreSqlPriceQueryBatchHandler(
    NpgsqlDataSource dataSource,
    BatchStartGate startGate,
    BatchExecutionCounter executionCounter) : IRequestBatchHandler<PriceQueryRequest>
{
    private const string BatchQuerySql = """
        SELECT product_id, price
        FROM benchmark_product_prices
        WHERE product_id = ANY(@product_ids);
        """;

    public async ValueTask HandleAsync(
        IReadOnlyList<PriceQueryRequest> requests,
        CancellationToken cancellationToken = default)
    {
        await startGate.WaitAsync().ConfigureAwait(false);

        var productIds = requests
            .Select(request => request.ProductId)
            .Distinct()
            .ToArray();
        var pricesByProductId = new Dictionary<long, decimal>(productIds.Length);

        await using var command = dataSource.CreateCommand(BatchQuerySql);
        command.Parameters.AddWithValue(
            "product_ids",
            NpgsqlDbType.Array | NpgsqlDbType.Bigint,
            productIds);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            pricesByProductId.Add(reader.GetInt64(0), reader.GetDecimal(1));
        }

        foreach (var request in requests)
        {
            request.Result = pricesByProductId.GetValueOrDefault(request.ProductId);
        }

        executionCounter.Record(requests.Count);
    }
}
