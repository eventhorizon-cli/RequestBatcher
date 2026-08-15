namespace RequestBatcher.Deduplication;

public sealed record PriceUpdate(long ProductId, long Version, decimal Price);
