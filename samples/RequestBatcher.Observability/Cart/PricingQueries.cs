namespace RequestBatcher.Observability.Cart;

internal static class PricingQueries
{
    internal static async ValueTask HandleAsync(
        IReadOnlyList<RequestBatchEntry<int, Product?>> entries,
        IHttpClientFactory clientFactory,
        CancellationToken cancellationToken)
    {
        var batchIds = entries.Select(entry => entry.Request).Distinct().ToArray();
        using var response = await clientFactory.CreateClient("pricing")
            .GetAsync($"prices?ids={string.Join(',', batchIds)}", cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                "The pricing service did not accept the product lookup.",
                inner: null,
                response.StatusCode);
        }

        var batchProducts = await response.Content
            .ReadFromJsonAsync<Product?[]>(cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (batchProducts is null || batchProducts.Length != batchIds.Length)
        {
            throw new InvalidOperationException("The pricing service returned an invalid product count.");
        }

        var productsById = new Dictionary<int, Product?>();
        for (var index = 0; index < batchIds.Length; index++)
        {
            var product = batchProducts[index];
            if (product is not null && (product.Id != batchIds[index] || product.Price < 0))
            {
                throw new InvalidOperationException("The pricing service returned an invalid product.");
            }

            productsById.Add(batchIds[index], product);
        }

        entries.SetResponses(id => productsById[id]);
    }
}
