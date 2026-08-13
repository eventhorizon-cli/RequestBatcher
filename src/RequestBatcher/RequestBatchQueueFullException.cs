namespace RequestBatcher;

/// <summary>
/// The exception thrown when a coordinator configured with <see cref="RequestBatchFullMode.Fail"/>
/// has no capacity for another request.
/// </summary>
public sealed class RequestBatchQueueFullException : InvalidOperationException
{
    internal RequestBatchQueueFullException(int capacity)
        : base($"The request batcher is full. Its pending-request capacity is {capacity}.")
    {
        Capacity = capacity;
    }

    /// <summary>Gets the configured pending-request capacity.</summary>
    public int Capacity { get; }
}
