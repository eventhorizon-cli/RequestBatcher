namespace RequestBatcher.Handlers;

internal delegate ValueTask RequestBatchHandler<TRequest>(
    IReadOnlyList<TRequest> requests,
    CancellationToken cancellationToken);
