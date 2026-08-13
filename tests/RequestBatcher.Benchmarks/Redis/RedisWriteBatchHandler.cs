using RequestBatcher.Benchmarks.Infrastructure;
using StackExchange.Redis;

namespace RequestBatcher.Benchmarks.Redis;

internal sealed class RedisWriteBatchHandler(
    IDatabase database,
    BatchStartGate startGate,
    BatchExecutionCounter executionCounter) : IRequestBatchHandler<RedisWriteRequest>
{
    public async ValueTask HandleAsync(
        IReadOnlyList<RedisWriteRequest> requests,
        CancellationToken cancellationToken = default)
    {
        await startGate.WaitAsync().ConfigureAwait(false);

        var entries = new KeyValuePair<RedisKey, RedisValue>[requests.Count];
        for (var index = 0; index < requests.Count; index++)
        {
            entries[index] = new KeyValuePair<RedisKey, RedisValue>(
                requests[index].Key,
                requests[index].Value);
        }

        var succeeded = await database
            .StringSetAsync(entries)
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!succeeded)
        {
            throw new InvalidOperationException("Redis rejected a batched string write.");
        }

        executionCounter.Record(requests.Count);
    }
}
