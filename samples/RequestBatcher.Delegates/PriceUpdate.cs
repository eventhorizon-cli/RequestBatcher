namespace RequestBatcher.Delegates;

/// <summary>
/// Represents a versioned product price submitted to the write batcher.
/// </summary>
/// <param name="ProductId">The positive product identifier.</param>
/// <param name="Version">The positive version used to reject stale updates.</param>
/// <param name="Price">The non-negative price.</param>
public sealed record PriceUpdate(long ProductId, long Version, decimal Price);
