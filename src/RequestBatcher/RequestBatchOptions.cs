using System.Numerics;
using BufferQueue.Memory;
using RequestBatcher.PendingRequests;

namespace RequestBatcher;

/// <summary>
/// Configures request batching and processing concurrency.
/// </summary>
/// <typeparam name="TRequest">The request type.</typeparam>
public sealed class RequestBatchOptions<TRequest>
{
    /// <summary>
    /// Gets or sets the maximum number of requests passed to one handler invocation.
    /// </summary>
    public int BatchSize { get; set; } = 128;

    /// <summary>
    /// Gets or sets the maximum number of handler invocations that may run concurrently.
    /// </summary>
    public int MaxConcurrency { get; set; } = 1;

    /// <summary>
    /// Gets or sets the bounded capacity for requests waiting in the internal queue.
    /// In <see cref="RequestBatchFullMode.Wait"/> mode, an explicit submission may exceed this value and is admitted
    /// in consecutive capacity-sized slices. <see cref="RequestBatchFullMode.Fail"/> rejects an oversized submission.
    /// </summary>
    public int MaxPendingRequests { get; set; } = 8192;

    /// <summary>
    /// Gets or sets how new requests behave when <see cref="MaxPendingRequests"/> is reached.
    /// </summary>
    public RequestBatchFullMode FullMode { get; set; } = RequestBatchFullMode.Wait;

    internal Action<MemoryBufferQueueOptions<PendingBatchRequest<TRequest>>>? ConfigurePartitionKey
    {
        get;
        private set;
    }

    /// <summary>
    /// Routes requests with equal finite, integer-valued numeric keys to the same queue partition.
    /// Routing does not guarantee handler ordering or serial execution.
    /// </summary>
    /// <typeparam name="TNumber">The numeric key type.</typeparam>
    /// <param name="partitionKeySelector">A deterministic, side-effect-free function that selects a request key.</param>
    public void UsePartitionKey<TNumber>(Func<TRequest, TNumber> partitionKeySelector)
        where TNumber : INumber<TNumber>
    {
        ArgumentNullException.ThrowIfNull(partitionKeySelector);
        ConfigurePartitionKey = options =>
            options.UsePartitionKey(pendingRequest => partitionKeySelector(pendingRequest.Request));
    }

    /// <summary>
    /// Routes requests with equal string keys to the same queue partition.
    /// Routing does not guarantee handler ordering or serial execution.
    /// </summary>
    /// <param name="partitionKeySelector">
    /// A deterministic, side-effect-free function that selects a non-null request key.
    /// </param>
    public void UsePartitionKey(Func<TRequest, string> partitionKeySelector)
    {
        ArgumentNullException.ThrowIfNull(partitionKeySelector);
        ConfigurePartitionKey = options =>
            options.UsePartitionKey(pendingRequest => partitionKeySelector(pendingRequest.Request));
    }

    internal RequestBatchOptions<TRequest> ValidateAndClone()
    {
        if (BatchSize <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(BatchSize), BatchSize, "Batch size must be greater than zero.");
        }

        if (MaxConcurrency <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxConcurrency), MaxConcurrency, "Maximum concurrency must be greater than zero.");
        }

        if (MaxPendingRequests <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxPendingRequests), MaxPendingRequests, "Maximum pending requests must be greater than zero.");
        }

        if (!Enum.IsDefined(FullMode))
        {
            throw new ArgumentOutOfRangeException(nameof(FullMode), FullMode, "Unknown full mode.");
        }

        return new RequestBatchOptions<TRequest>
        {
            BatchSize = BatchSize,
            MaxConcurrency = MaxConcurrency,
            MaxPendingRequests = MaxPendingRequests,
            FullMode = FullMode,
            ConfigurePartitionKey = ConfigurePartitionKey,
        };
    }

    internal void CopyFrom(RequestBatchOptions<TRequest> source)
    {
        ArgumentNullException.ThrowIfNull(source);

        BatchSize = source.BatchSize;
        MaxConcurrency = source.MaxConcurrency;
        MaxPendingRequests = source.MaxPendingRequests;
        FullMode = source.FullMode;
        ConfigurePartitionKey = source.ConfigurePartitionKey;
    }
}
