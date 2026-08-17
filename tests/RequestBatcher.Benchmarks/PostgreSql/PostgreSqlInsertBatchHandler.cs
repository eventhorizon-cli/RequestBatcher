using Npgsql;
using NpgsqlTypes;
using RequestBatcher.Benchmarks.Infrastructure;

namespace RequestBatcher.Benchmarks.PostgreSql;

internal sealed class PostgreSqlInsertBatchHandler(
    NpgsqlDataSource dataSource,
    BatchStartGate startGate,
    BatchExecutionCounter executionCounter) : IRequestBatchHandler<InsertRequest>
{
    private const string InsertBatchSql = """
        INSERT INTO benchmark_writes (request_id, payload)
        SELECT request_id, payload
        FROM unnest(@request_ids, @payloads) AS rows(request_id, payload);
        """;

    public async ValueTask HandleAsync(
        IReadOnlyList<InsertRequest> requests,
        CancellationToken cancellationToken = default)
    {
        await startGate.WaitAsync().ConfigureAwait(false);

        var requestIds = new int[requests.Count];
        var payloads = new string[requests.Count];
        for (var index = 0; index < requests.Count; index++)
        {
            requestIds[index] = requests[index].RequestId;
            payloads[index] = requests[index].Payload;
        }

        await using var command = dataSource.CreateCommand(InsertBatchSql);
        command.Parameters.AddWithValue(
            "request_ids",
            NpgsqlDbType.Array | NpgsqlDbType.Integer,
            requestIds);
        command.Parameters.AddWithValue(
            "payloads",
            NpgsqlDbType.Array | NpgsqlDbType.Text,
            payloads);

        var affectedRows = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (affectedRows != requests.Count)
        {
            throw new InvalidOperationException(
                $"Expected to insert {requests.Count} rows, but PostgreSQL reported {affectedRows}.");
        }

        executionCounter.Record(requests.Count);
    }
}
