# Duplicate Update Merging

This sample batches versioned price updates and prevents stale duplicates from overwriting newer data.

- `UsePartitionKey(update => update.ProductId)` prevents updates for one product from executing concurrently while
  allowing different products to use multiple partitions.
- `PriceUpdateHandler` retains only the highest version for each product within one batch.
- `InMemoryPriceStore` applies a version check across batches, because opportunistic batching is not a global
  deduplication boundary.

All callers still await their own `Task`. Superseded updates complete successfully after the handler persists the
winning update for their batch.

Run the sample from the repository root:

```bash
dotnet run --project samples/RequestBatcher.Deduplication/RequestBatcher.Deduplication.csproj
```
