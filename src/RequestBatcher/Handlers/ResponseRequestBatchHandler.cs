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

        var count = entries.Count;
        for (var index = 0; index < count; index++)
        {
            entries[index].EnsureResponseIsSet();
        }
    }
}
