namespace RequestBatcher;

/// <summary>
/// The exception thrown when a coordinator cannot admit a request submission because capacity is unavailable or the
/// submission itself exceeds the configured capacity.
/// </summary>
public sealed class RequestBatchQueueFullException : InvalidOperationException
{
    internal RequestBatchQueueFullException(int capacity, int requestedCount = 1)
        : base(
            $"The request batcher cannot admit {requestedCount} request(s). " +
            $"Its pending-request capacity is {capacity}.")
    {
        Capacity = capacity;
        RequestedCount = requestedCount;
    }

    /// <summary>Gets the configured pending-request capacity.</summary>
    public int Capacity { get; }

    /// <summary>Gets the number of requests in the rejected submission.</summary>
    public int RequestedCount { get; }
}
