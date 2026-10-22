namespace RequestBatcher.Observability.Cart;

internal sealed record QuoteLine(
    int ProductId,
    string Name,
    decimal UnitPrice,
    int Quantity,
    decimal LineTotal);
