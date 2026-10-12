using System.Numerics;
using BufferQueue.Memory;
using RequestBatcher.PendingRequests;

namespace RequestBatcher;

internal abstract class RequestBatchPartitionKey<TRequest>
{
    public abstract int PartitionCount { get; }

    public abstract void Configure(MemoryBufferQueueOptions<PendingBatchRequest<TRequest>> options);

    public abstract RequestBatchPartitionKey<TWrapped> Project<TWrapped>(
        Func<TWrapped, TRequest> requestSelector);

    public static RequestBatchPartitionKey<TRequest> Create<TNumber>(
        Func<TRequest, TNumber> selector,
        int partitionCount)
        where TNumber : INumber<TNumber> =>
        new NumericPartitionKey<TNumber>(selector, partitionCount);

    public static RequestBatchPartitionKey<TRequest> Create(Func<TRequest, string> selector, int partitionCount) =>
        new StringPartitionKey(selector, partitionCount);

    private sealed class NumericPartitionKey<TNumber>(Func<TRequest, TNumber> selector, int partitionCount)
        : RequestBatchPartitionKey<TRequest>
        where TNumber : INumber<TNumber>
    {
        public override int PartitionCount => partitionCount;

        public override void Configure(MemoryBufferQueueOptions<PendingBatchRequest<TRequest>> options) =>
            options.UsePartitionKey(pendingRequest => selector(pendingRequest.Request));

        public override RequestBatchPartitionKey<TWrapped> Project<TWrapped>(
            Func<TWrapped, TRequest> requestSelector) =>
            RequestBatchPartitionKey<TWrapped>.Create<TNumber>(
                request => selector(requestSelector(request)),
                partitionCount);
    }

    private sealed class StringPartitionKey(Func<TRequest, string> selector, int partitionCount)
        : RequestBatchPartitionKey<TRequest>
    {
        public override int PartitionCount => partitionCount;

        public override void Configure(MemoryBufferQueueOptions<PendingBatchRequest<TRequest>> options) =>
            options.UsePartitionKey(pendingRequest => selector(pendingRequest.Request));

        public override RequestBatchPartitionKey<TWrapped> Project<TWrapped>(
            Func<TWrapped, TRequest> requestSelector) =>
            RequestBatchPartitionKey<TWrapped>.Create(
                request => selector(requestSelector(request)),
                partitionCount);
    }
}
