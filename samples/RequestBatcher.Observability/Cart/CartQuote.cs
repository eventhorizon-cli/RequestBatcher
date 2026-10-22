namespace RequestBatcher.Observability.Cart;

internal sealed record CartQuote(IReadOnlyList<QuoteLine> Lines, decimal Total);
