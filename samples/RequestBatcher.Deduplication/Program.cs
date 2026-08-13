using global::RequestBatcher;
using Microsoft.Extensions.DependencyInjection;
using RequestBatcher.Deduplication;

var services = new ServiceCollection();
services.AddSingleton<InMemoryPriceStore>();
services.AddRequestBatcher<PriceUpdate, PriceUpdateHandler>(
    ServiceLifetime.Singleton,
    options =>
    {
        options.BatchSize = 100;
        options.MaxConcurrency = 4;
        options.UsePartitionKey(update => update.ProductId);
    });

await using var serviceProvider = services.BuildServiceProvider();
var batcher = serviceProvider.GetRequiredService<IRequestBatcher<PriceUpdate>>();

PriceUpdate[] incomingUpdates =
[
    new(ProductId: 101, Version: 1, Price: 19.90m),
    new(ProductId: 202, Version: 3, Price: 42.00m),
    new(ProductId: 101, Version: 3, Price: 17.90m),
    new(ProductId: 101, Version: 2, Price: 18.50m),
    new(ProductId: 202, Version: 2, Price: 45.00m),
];

await Task.WhenAll(incomingUpdates.Select(update => batcher.ProcessAsync(update)));

var store = serviceProvider.GetRequiredService<InMemoryPriceStore>();
foreach (var price in store.GetAll())
{
    Console.WriteLine(
        $"Product {price.ProductId}: version {price.Version}, price {price.Price:F2}");
}
