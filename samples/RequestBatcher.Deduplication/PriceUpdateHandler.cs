namespace RequestBatcher.Deduplication;

internal sealed class PriceUpdateHandler(InMemoryPriceStore store)
    : IRequestBatchHandler<PriceUpdate>
{
    public ValueTask HandleAsync(
        IReadOnlyList<PriceUpdate> requests,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var latestUpdates = requests
            .GroupBy(request => request.ProductId)
            .Select(group => group.MaxBy(request => request.Version)!)
            .ToArray();

        store.UpsertLatest(latestUpdates);
        return ValueTask.CompletedTask;
    }
}
