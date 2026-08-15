namespace RequestBatcher.Deduplication;

internal sealed class PriceQuery(long productId)
{
    public long ProductId { get; } = productId;

    // IRequestBatcher reports completion rather than returning Task<TResult>, so the request carries its result.
    public PriceUpdate? Result { get; private set; }

    public void SetResult(PriceUpdate? result) => Result = result;
}
