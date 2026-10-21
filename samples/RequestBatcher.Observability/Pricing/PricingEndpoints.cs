using Microsoft.AspNetCore.Mvc;

namespace RequestBatcher.Observability.Pricing;

internal static class PricingEndpoints
{
    internal static void MapPricingEndpoints(this WebApplication app)
    {
        app.MapGet("/prices/{id:int}", async (int id,
            [FromKeyedServices("product-reads")] IRequestBatcher<int, Product?> batcher,
            CancellationToken cancellationToken) =>
        {
            var product = await batcher.ExecuteAsync(id, cancellationToken);
            return product is null ? Results.NotFound() : Results.Ok(product);
        });
        app.MapGet("/prices", async ([FromQuery] string ids,
            [FromKeyedServices("product-reads")] IRequestBatcher<int, Product?> batcher,
            CancellationToken cancellationToken) =>
        {
            var parts = ids.Split(',');
            if (parts.Length is < 1 or > 64)
            {
                return Results.BadRequest("Provide between 1 and 64 comma-separated product IDs.");
            }

            var parsed = new int[parts.Length];
            for (var index = 0; index < parts.Length; index++)
            {
                if (!int.TryParse(parts[index], out parsed[index]) || parsed[index] <= 0)
                {
                    return Results.BadRequest("Product IDs must be positive integers.");
                }
            }

            return Results.Ok(await batcher.ExecuteAsync(parsed, cancellationToken));
        });
    }

}
