using System.Numerics;
using BufferQueue.Memory;
using RequestBatcher.PendingRequests;

namespace RequestBatcher;

internal abstract class RequestBatchPartitionKey<TRequest>
{
    public abstract void Configure(MemoryBufferQueueOptions<PendingBatchRequest<TRequest>> options);

    public abstract RequestBatchPartitionKey<TWrapped> Project<TWrapped>(
        Func<TWrapped, TRequest> requestSelector);

    public static RequestBatchPartitionKey<TRequest> Create<TNumber>(
        Func<TRequest, TNumber> selector)
        where TNumber : INumber<TNumber> =>
        new NumericPartitionKey<TNumber>(selector);

    public static RequestBatchPartitionKey<TRequest> Create(Func<TRequest, string> selector) =>
        new StringPartitionKey(selector);

    private sealed class NumericPartitionKey<TNumber>(Func<TRequest, TNumber> selector)
        : RequestBatchPartitionKey<TRequest>
        where TNumber : INumber<TNumber>
    {
        private readonly Func<TRequest, TNumber> _selector = selector;

        public override void Configure(MemoryBufferQueueOptions<PendingBatchRequest<TRequest>> options) =>
            options.UsePartitionKey(pendingRequest => _selector(pendingRequest.Request));

        public override RequestBatchPartitionKey<TWrapped> Project<TWrapped>(
            Func<TWrapped, TRequest> requestSelector) =>
            RequestBatchPartitionKey<TWrapped>.Create<TNumber>(
                request => _selector(requestSelector(request)));
    }

    private sealed class StringPartitionKey(Func<TRequest, string> selector)
        : RequestBatchPartitionKey<TRequest>
    {
        private readonly Func<TRequest, string> _selector = selector;

        public override void Configure(MemoryBufferQueueOptions<PendingBatchRequest<TRequest>> options) =>
            options.UsePartitionKey(pendingRequest => _selector(pendingRequest.Request));

        public override RequestBatchPartitionKey<TWrapped> Project<TWrapped>(
            Func<TWrapped, TRequest> requestSelector) =>
            RequestBatchPartitionKey<TWrapped>.Create(
                request => _selector(requestSelector(request)));
    }
}
