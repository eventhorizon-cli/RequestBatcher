namespace RequestBatcher.Benchmarks.PostgreSql;

internal sealed class PriceQueryRequest(long productId)
{
    public long ProductId { get; } = productId;

    public decimal? Result { get; set; }
}
