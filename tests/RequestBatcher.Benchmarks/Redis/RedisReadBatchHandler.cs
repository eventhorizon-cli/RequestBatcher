using RequestBatcher.Benchmarks.Infrastructure;
using StackExchange.Redis;

namespace RequestBatcher.Benchmarks.Redis;

internal sealed class RedisReadBatchHandler(
    IDatabase database,
    BatchStartGate startGate,
    BatchExecutionCounter executionCounter) : IRequestBatchHandler<RedisReadRequest>
{
    public async ValueTask HandleAsync(
        IReadOnlyList<RedisReadRequest> requests,
        CancellationToken cancellationToken = default)
    {
        await startGate.WaitAsync().ConfigureAwait(false);

        var keys = new RedisKey[requests.Count];
        for (var index = 0; index < requests.Count; index++)
        {
            keys[index] = requests[index].Key;
        }

        var values = await database
            .StringGetAsync(keys)
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        if (values.Length != requests.Count)
        {
            throw new InvalidOperationException(
                $"Redis returned {values.Length} values; expected {requests.Count}.");
        }

        for (var index = 0; index < requests.Count; index++)
        {
            requests[index].Result = values[index];
        }

        executionCounter.Record(requests.Count);
    }
}
