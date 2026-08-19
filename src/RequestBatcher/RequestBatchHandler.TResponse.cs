namespace RequestBatcher;

/// <summary>
/// Represents a function that processes a batch of response-bearing requests.
/// </summary>
/// <typeparam name="TRequest">The request type.</typeparam>
/// <typeparam name="TResponse">The response type.</typeparam>
/// <param name="requests">The request items selected for one handler invocation.</param>
/// <param name="cancellationToken">A token that cancels the RequestBatcher processing lifecycle.</param>
public delegate ValueTask RequestBatchHandler<TRequest, TResponse>(
    IReadOnlyList<RequestBatchItem<TRequest, TResponse>> requests,
    CancellationToken cancellationToken);
