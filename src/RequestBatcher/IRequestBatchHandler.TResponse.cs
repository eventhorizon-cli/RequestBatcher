namespace RequestBatcher;

/// <summary>
/// Processes one opportunistically collected batch of response-bearing requests.
/// </summary>
/// <typeparam name="TRequest">The request type.</typeparam>
/// <typeparam name="TResponse">The response type.</typeparam>
public interface IRequestBatchHandler<TRequest, TResponse>
{
    /// <summary>
    /// Processes the batch and sets exactly one response on every supplied item before returning successfully.
    /// </summary>
    /// <param name="requests">The request items selected for this handler invocation.</param>
    /// <param name="cancellationToken">A token that cancels the RequestBatcher processing lifecycle.</param>
    /// <returns>A task-like value that completes after processing and response assignment finish.</returns>
    /// <remarks>
    /// <see cref="RequestBatchItem{TRequest, TResponse}.SetResponse(TResponse)"/> may be called individually, or
    /// <see cref="RequestBatchItemExtensions.SetResponses{TRequest, TResponse}(IReadOnlyList{RequestBatchItem{TRequest, TResponse}}, IEnumerable{TResponse})"/>
    /// may assign responses in input order. Returning without a response for every item fails the handler batch.
    /// </remarks>
    ValueTask HandleAsync(
        IReadOnlyList<RequestBatchItem<TRequest, TResponse>> requests,
        CancellationToken cancellationToken = default);
}
