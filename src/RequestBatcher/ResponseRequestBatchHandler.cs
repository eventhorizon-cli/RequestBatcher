namespace RequestBatcher;

internal sealed class ResponseRequestBatchHandler<TRequest, TResponse>(
    IRequestBatchHandler<TRequest, TResponse> handler)
    : IRequestBatchHandler<RequestBatchItem<TRequest, TResponse>>
{
    public async ValueTask HandleAsync(
        IReadOnlyList<RequestBatchItem<TRequest, TResponse>> requests,
        CancellationToken cancellationToken = default)
    {
        await handler.HandleAsync(requests, cancellationToken).ConfigureAwait(false);

        foreach (var request in requests)
        {
            _ = request.GetResponse();
        }
    }
}
