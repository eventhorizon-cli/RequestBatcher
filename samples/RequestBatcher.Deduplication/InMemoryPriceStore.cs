using System.Collections.Concurrent;

namespace RequestBatcher.Deduplication;

internal sealed class InMemoryPriceStore
{
    private readonly ConcurrentDictionary<long, ProductPrice> _prices = new();

    public void UpsertLatest(IEnumerable<PriceUpdate> updates)
    {
        foreach (var update in updates)
        {
            _prices.AddOrUpdate(
                update.ProductId,
                _ => new ProductPrice(update.ProductId, update.Version, update.Price),
                (_, current) => update.Version > current.Version
                    ? new ProductPrice(update.ProductId, update.Version, update.Price)
                    : current);
        }
    }

    public IReadOnlyList<ProductPrice> GetAll() =>
        _prices.Values.OrderBy(price => price.ProductId).ToArray();
}
