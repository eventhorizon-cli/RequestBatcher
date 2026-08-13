namespace RequestBatcher.Benchmarks.Infrastructure;

internal sealed class BatchExecutionCounter
{
    private int _batchCount;
    private int _maximumBatchSize;
    private int _requestCount;

    public BatchExecutionSnapshot Snapshot => new(
        Volatile.Read(ref _batchCount),
        Volatile.Read(ref _requestCount),
        Volatile.Read(ref _maximumBatchSize));

    public void Record(int batchSize)
    {
        Interlocked.Increment(ref _batchCount);
        Interlocked.Add(ref _requestCount, batchSize);

        var currentMaximum = Volatile.Read(ref _maximumBatchSize);
        while (currentMaximum < batchSize)
        {
            var observed = Interlocked.CompareExchange(
                ref _maximumBatchSize,
                batchSize,
                currentMaximum);
            if (observed == currentMaximum)
            {
                break;
            }

            currentMaximum = observed;
        }
    }

    public void Reset()
    {
        Volatile.Write(ref _batchCount, 0);
        Volatile.Write(ref _requestCount, 0);
        Volatile.Write(ref _maximumBatchSize, 0);
    }
}
