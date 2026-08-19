using System.Numerics;

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

    internal RequestBatchPartitionKey<TRequest>? PartitionKey
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
        PartitionKey = RequestBatchPartitionKey<TRequest>.Create(partitionKeySelector);
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
        PartitionKey = RequestBatchPartitionKey<TRequest>.Create(partitionKeySelector);
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
            PartitionKey = PartitionKey,
        };
    }

    internal RequestBatchOptions<TWrapped> Project<TWrapped>(Func<TWrapped, TRequest> requestSelector)
    {
        ArgumentNullException.ThrowIfNull(requestSelector);

        return new RequestBatchOptions<TWrapped>
        {
            BatchSize = BatchSize,
            MaxConcurrency = MaxConcurrency,
            MaxPendingRequests = MaxPendingRequests,
            FullMode = FullMode,
            PartitionKey = PartitionKey?.Project(requestSelector),
        };
    }

    internal void CopyFrom(RequestBatchOptions<TRequest> source)
    {
        ArgumentNullException.ThrowIfNull(source);

        BatchSize = source.BatchSize;
        MaxConcurrency = source.MaxConcurrency;
        MaxPendingRequests = source.MaxPendingRequests;
        FullMode = source.FullMode;
        PartitionKey = source.PartitionKey;
    }
}
