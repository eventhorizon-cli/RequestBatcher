namespace RequestBatcher.Deduplication;

internal sealed class PriceQuery(long productId)
{
    public long ProductId { get; } = productId;
}
