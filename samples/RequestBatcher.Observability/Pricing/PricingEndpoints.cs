using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace RequestBatcher.Observability.Pricing;

internal static class PricingEndpoints
{
    internal static void MapPricingEndpoints(this WebApplication app)
    {
        app.MapGet("/prices/{id:int}", async (int id,
            NpgsqlDataSource dataSource,
            CancellationToken cancellationToken) =>
        {
            var product = (await QueryProductsAsync(dataSource, [id], cancellationToken).ConfigureAwait(false))[0];
            return product is null ? Results.NotFound() : Results.Ok(product);
        });
        app.MapGet("/prices", async ([FromQuery] string ids,
            NpgsqlDataSource dataSource,
            CancellationToken cancellationToken) =>
        {
            var parts = ids.Split(',');
            if (parts.Length is < 1 or > 20)
            {
                return Results.BadRequest("Provide between 1 and 20 comma-separated product IDs.");
            }

            var parsed = new int[parts.Length];
            for (var index = 0; index < parts.Length; index++)
            {
                if (!int.TryParse(parts[index], out parsed[index]) || parsed[index] <= 0)
                {
                    return Results.BadRequest("Product IDs must be positive integers.");
                }
            }

            return Results.Ok(await QueryProductsAsync(dataSource, parsed, cancellationToken).ConfigureAwait(false));
        });
    }

    private static async Task<Product?[]> QueryProductsAsync(
        NpgsqlDataSource dataSource,
        IReadOnlyList<int> ids,
        CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT id, name, price FROM products WHERE id = ANY($1)");
        command.Parameters.AddWithValue(ids.ToArray());
        var products = new Dictionary<int, Product>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var product = new Product(reader.GetInt32(0), reader.GetString(1), reader.GetDecimal(2));
            products[product.Id] = product;
        }

        return ids.Select(id => products.GetValueOrDefault(id)).ToArray();
    }
}
