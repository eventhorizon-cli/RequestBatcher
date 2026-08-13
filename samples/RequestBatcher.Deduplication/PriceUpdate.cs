namespace RequestBatcher.Deduplication;

internal sealed record PriceUpdate(long ProductId, long Version, decimal Price);
