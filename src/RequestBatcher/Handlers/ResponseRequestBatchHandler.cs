namespace RequestBatcher.Handlers;

internal sealed class ResponseRequestBatchHandler<TRequest, TResponse>(
    IRequestBatchHandler<TRequest, TResponse> handler)
    : IRequestBatchHandler<RequestBatchEntry<TRequest, TResponse>>
{
    public async ValueTask HandleAsync(
        IReadOnlyList<RequestBatchEntry<TRequest, TResponse>> entries,
        CancellationToken cancellationToken = default)
    {
        await handler.HandleAsync(entries, cancellationToken).ConfigureAwait(false);

        foreach (var entry in entries)
        {
            _ = entry.GetResponse();
        }
    }
}
