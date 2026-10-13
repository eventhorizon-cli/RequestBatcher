namespace RequestBatcher;

internal sealed class ResponseRequestBatchHandler<TRequest, TResponse>(
    IRequestBatchHandler<TRequest, TResponse> handler)
    : IRequestBatchHandler<RequestBatchItem<TRequest, TResponse>>
{
    public async ValueTask HandleAsync(
        IReadOnlyList<RequestBatchItem<TRequest, TResponse>> entries,
        CancellationToken cancellationToken = default)
    {
        await handler.HandleAsync(entries, cancellationToken).ConfigureAwait(false);

        foreach (var entry in entries)
        {
            _ = entry.GetResponse();
        }
    }
}
