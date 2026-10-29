namespace RequestBatcher.Observability.Cart;

internal sealed class PricingQueryHandler(IHttpClientFactory clientFactory)
    : IRequestBatchHandler<int, Product?>
{
    public ValueTask HandleAsync(
        IReadOnlyList<RequestBatchEntry<int, Product?>> entries,
        CancellationToken cancellationToken = default) =>
        PricingQueries.HandleAsync(entries, clientFactory, cancellationToken);
}
