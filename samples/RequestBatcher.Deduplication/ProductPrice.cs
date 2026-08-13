namespace RequestBatcher.Deduplication;

internal sealed record ProductPrice(long ProductId, long Version, decimal Price);
