using System.Text.Json;

namespace RequestBatcher.Observability.Cart;

internal static class CartEndpoints
{
    internal static void MapCartEndpoints(this WebApplication app)
    {
        app.MapPost("/cart/quote", async (
            CartItem?[]? items,
            [FromKeyedServices("pricing-queries")] IRequestBatcher<int, Product?> pricingBatcher,
            CancellationToken cancellationToken) =>
        {
            var validationError = Validate(items);
            if (validationError is not null)
            {
                return Results.BadRequest(validationError);
            }

            var validItems = items!.Select(item => item!).ToArray();
            var ids = validItems.Select(item => item.ProductId).ToArray();
            IReadOnlyList<Product?> products;
            try
            {
                // The native batch overload creates one call span for the entire Cart submission.
                products = await pricingBatcher.ExecuteAsync(
                    ids,
                    static async ValueTask (
                        IReadOnlyList<RequestBatchEntry<int, Product?>> entries,
                        IHttpClientFactory clientFactory,
                        CancellationToken batchCancellationToken) =>
                    {
                        var batchIds = entries.Select(entry => entry.Request).Distinct().ToArray();
                        using var response = await clientFactory.CreateClient("pricing")
                            .GetAsync($"prices?ids={string.Join(',', batchIds)}", batchCancellationToken)
                            .ConfigureAwait(false);
                        if (!response.IsSuccessStatusCode)
                        {
                            throw new HttpRequestException(
                                "The pricing service did not accept the product lookup.",
                                inner: null,
                                response.StatusCode);
                        }

                        var batchProducts = await response.Content
                            .ReadFromJsonAsync<Product?[]>(cancellationToken: batchCancellationToken)
                            .ConfigureAwait(false);
                        if (batchProducts is null || batchProducts.Length != batchIds.Length)
                        {
                            throw new InvalidOperationException(
                                "The pricing service returned an invalid product count.");
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
                    },
                    cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                return Results.Problem(
                    "The pricing service timed out.",
                    statusCode: StatusCodes.Status504GatewayTimeout);
            }
            catch (HttpRequestException)
            {
                return Results.Problem(
                    "The pricing service could not be reached.",
                    statusCode: StatusCodes.Status502BadGateway);
            }
            catch (Exception exception) when (exception is InvalidOperationException or JsonException)
            {
                return Results.Problem(
                    "The pricing service returned an invalid response.",
                    statusCode: StatusCodes.Status502BadGateway);
            }

            if (products.Count != validItems.Length)
            {
                return Results.Problem(
                    "The pricing service returned an invalid product count.",
                    statusCode: StatusCodes.Status502BadGateway);
            }

            var lines = new QuoteLine[validItems.Length];
            var total = 0m;
            for (var index = 0; index < validItems.Length; index++)
            {
                var product = products[index];
                if (product is null)
                {
                    return Results.NotFound($"Product {validItems[index].ProductId} was not found.");
                }

                var item = validItems[index];
                if (product.Id != item.ProductId || product.Price < 0)
                {
                    return Results.Problem(
                        "The pricing service returned an invalid product.",
                        statusCode: StatusCodes.Status502BadGateway);
                }

                var lineTotal = product.Price * item.Quantity;
                lines[index] = new QuoteLine(product.Id, product.Name, product.Price, item.Quantity, lineTotal);
                total += lineTotal;
            }

            return Results.Ok(new CartQuote(lines, total));
        });
    }

    private static string? Validate(CartItem?[]? items)
    {
        if (items is null || items.Length is < 2 or > 10)
        {
            return "Provide between 2 and 10 cart items.";
        }

        for (var index = 0; index < items.Length; index++)
        {
            var item = items[index];
            if (item is null || item.ProductId <= 0)
            {
                return $"Cart item {index} must have a positive product ID.";
            }

            if (item.Quantity is < 1 or > 100)
            {
                return $"Cart item {index} quantity must be between 1 and 100.";
            }
        }

        return null;
    }
}
