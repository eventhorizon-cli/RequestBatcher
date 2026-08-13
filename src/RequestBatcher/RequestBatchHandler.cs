namespace RequestBatcher;

/// <summary>
/// Represents a function that processes a batch of requests.
/// </summary>
public delegate ValueTask RequestBatchHandler<TRequest>(
    IReadOnlyList<TRequest> requests,
    CancellationToken cancellationToken);
