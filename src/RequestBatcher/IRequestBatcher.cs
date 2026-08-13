namespace RequestBatcher;

/// <summary>
/// Accepts individual requests and completes each call after its containing batch is processed.
/// </summary>
/// <typeparam name="TRequest">The request type.</typeparam>
public interface IRequestBatcher<in TRequest>
{
    /// <summary>
    /// Enqueues a request and asynchronously waits for its batch to finish.
    /// </summary>
    Task ProcessAsync(TRequest request, CancellationToken cancellationToken = default);
}
