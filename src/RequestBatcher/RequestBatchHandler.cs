namespace RequestBatcher;

internal delegate ValueTask RequestBatchHandler<TRequest>(
    IReadOnlyList<TRequest> requests,
    CancellationToken cancellationToken);
