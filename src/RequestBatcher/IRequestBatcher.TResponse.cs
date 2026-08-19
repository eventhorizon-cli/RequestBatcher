namespace RequestBatcher;

/// <summary>
/// Accepts individual or explicitly grouped requests and returns one response for each successfully processed request.
/// </summary>
/// <typeparam name="TRequest">The request type.</typeparam>
/// <typeparam name="TResponse">The response type.</typeparam>
public interface IRequestBatcher<in TRequest, TResponse>
{
    /// <summary>
    /// Enqueues a request and asynchronously returns its response after the handler processes its batch successfully.
    /// </summary>
    /// <param name="request">The request to enqueue.</param>
    /// <param name="cancellationToken">A token that cancels the request before handler dispatch.</param>
    /// <returns>A task that returns the response for <paramref name="request"/>.</returns>
    Task<TResponse> ProcessAsync(TRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Enqueues multiple requests with one production operation and asynchronously returns their responses.
    /// </summary>
    /// <param name="requests">The requests to enqueue.</param>
    /// <param name="cancellationToken">A token that cancels requests before handler dispatch.</param>
    /// <returns>
    /// A task that returns one response for every supplied request. The response at each index corresponds to the
    /// request at the same index in the input sequence, even when the requests are split across handler batches or
    /// queue partitions.
    /// </returns>
    Task<IReadOnlyList<TResponse>> ProcessAsync(
        IEnumerable<TRequest> requests,
        CancellationToken cancellationToken = default);
}
