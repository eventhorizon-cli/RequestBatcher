namespace RequestBatcher;

/// <summary>
/// Processes one opportunistically collected batch of requests.
/// </summary>
/// <typeparam name="TRequest">The request type.</typeparam>
public interface IRequestBatchHandler<TRequest>
{
    /// <summary>
    /// Processes the batch. Returning successfully acknowledges every request in the batch;
    /// throwing fails every request in the batch with the same exception.
    /// </summary>
    ValueTask HandleAsync(
        IReadOnlyList<TRequest> requests,
        CancellationToken cancellationToken = default);
}
