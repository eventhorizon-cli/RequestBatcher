namespace RequestBatcher;

/// <summary>
/// Accepts individual or explicitly grouped requests and completes each call after its requests are processed.
/// </summary>
/// <typeparam name="TRequest">The request type.</typeparam>
public interface IRequestBatcher<in TRequest>
{
    /// <summary>
    /// Enqueues a request and asynchronously waits for its batch to finish.
    /// </summary>
    Task ProcessAsync(TRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Enqueues multiple requests with one production operation and asynchronously waits for every request to finish.
    /// The requests may still be split across handler invocations according to the configured batch size and partitions.
    /// If several handler invocations fail, the returned task retains each distinct exception instance.
    /// </summary>
    Task ProcessAsync(
        IEnumerable<TRequest> requests,
        CancellationToken cancellationToken = default);
}
