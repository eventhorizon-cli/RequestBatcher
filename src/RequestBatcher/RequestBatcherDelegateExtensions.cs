using System.Diagnostics.CodeAnalysis;
using RequestBatcher.DelegateHandlers;

namespace RequestBatcher;

/// <summary>
/// Submits requests with a static delegate handler to a delegate-enabled request batcher.
/// </summary>
public static class RequestBatcherDelegateExtensions
{
    private const string DynamicCodeMessage = "Delegate handlers compile a typed invoker on first use.";
    private const string TrimmingMessage =
        "Delegate handlers inspect the delegate signature and service parameter attributes.";

    /// <summary>
    /// Submits one request using the pipeline's single delegate handler.
    /// </summary>
    /// <typeparam name="TRequest">The request type.</typeparam>
    /// <param name="batcher">A batcher registered with AddDelegateRequestBatcher.</param>
    /// <param name="request">The request to submit.</param>
    /// <param name="handler">A delegate to a static lambda, anonymous method, local function, or method receiving the batch, DI services and an optional batch token.</param>
    /// <param name="cancellationToken">Cancels the request only before dispatch.</param>
    /// <returns>A task that completes after handler execution and batch scope disposal.</returns>
    /// <remarks>The first handler binds the pipeline; subsequent calls must supply an equal delegate.</remarks>
    [RequiresDynamicCode(DynamicCodeMessage)]
    [RequiresUnreferencedCode(TrimmingMessage)]
    public static Task ExecuteAsync<TRequest>(
        this IRequestBatcher<TRequest> batcher,
        TRequest request,
        Delegate handler,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batcher);
        ArgumentNullException.ThrowIfNull(handler);
        return GetDelegateBatcher(batcher).SubmitAsync(request, handler, cancellationToken);
    }

    /// <summary>
    /// Submits a request sequence using the pipeline's single delegate handler.
    /// </summary>
    /// <typeparam name="TRequest">The request type.</typeparam>
    /// <param name="batcher">A batcher registered with AddDelegateRequestBatcher.</param>
    /// <param name="requests">The requests to submit; they may span multiple handler batches.</param>
    /// <param name="handler">A delegate to a static lambda, anonymous method, local function, or method receiving the batch, DI services and an optional batch token.</param>
    /// <param name="cancellationToken">Cancels requests only before dispatch.</param>
    /// <returns>A task that completes after all submitted requests complete.</returns>
    /// <remarks>The first handler binds the pipeline; subsequent calls must supply an equal delegate.</remarks>
    [RequiresDynamicCode(DynamicCodeMessage)]
    [RequiresUnreferencedCode(TrimmingMessage)]
    public static Task ExecuteAsync<TRequest>(
        this IRequestBatcher<TRequest> batcher,
        IEnumerable<TRequest> requests,
        Delegate handler,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batcher);
        ArgumentNullException.ThrowIfNull(requests);
        ArgumentNullException.ThrowIfNull(handler);
        return GetDelegateBatcher(batcher).SubmitAsync(requests, handler, cancellationToken);
    }

    /// <summary>
    /// Submits one request and returns its response using the pipeline's single delegate handler.
    /// </summary>
    /// <typeparam name="TRequest">The request type.</typeparam>
    /// <typeparam name="TResponse">The response type.</typeparam>
    /// <param name="batcher">A batcher registered with AddDelegateRequestBatcher.</param>
    /// <param name="request">The request to submit.</param>
    /// <param name="handler">A delegate to a static lambda, anonymous method, local function, or method receiving response entries, DI services and an optional batch token.</param>
    /// <param name="cancellationToken">Cancels the request only before dispatch.</param>
    /// <returns>A task containing the response after handler execution and batch scope disposal.</returns>
    /// <remarks>The first handler binds the pipeline; subsequent calls must supply an equal delegate.</remarks>
    [RequiresDynamicCode(DynamicCodeMessage)]
    [RequiresUnreferencedCode(TrimmingMessage)]
    public static Task<TResponse> ExecuteAsync<TRequest, TResponse>(
        this IRequestBatcher<TRequest, TResponse> batcher,
        TRequest request,
        Delegate handler,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batcher);
        ArgumentNullException.ThrowIfNull(handler);
        return GetDelegateBatcher(batcher).SubmitAsync(request, handler, cancellationToken);
    }

    /// <summary>
    /// Submits a request sequence and returns responses in input order using the pipeline's single delegate handler.
    /// </summary>
    /// <typeparam name="TRequest">The request type.</typeparam>
    /// <typeparam name="TResponse">The response type.</typeparam>
    /// <param name="batcher">A batcher registered with AddDelegateRequestBatcher.</param>
    /// <param name="requests">The requests to submit; they may span multiple handler batches.</param>
    /// <param name="handler">A delegate to a static lambda, anonymous method, local function, or method receiving response entries, DI services and an optional batch token.</param>
    /// <param name="cancellationToken">Cancels requests only before dispatch.</param>
    /// <returns>A task containing one response per input, including duplicates, after all requests complete.</returns>
    /// <remarks>The first handler binds the pipeline; subsequent calls must supply an equal delegate.</remarks>
    [RequiresDynamicCode(DynamicCodeMessage)]
    [RequiresUnreferencedCode(TrimmingMessage)]
    public static Task<IReadOnlyList<TResponse>> ExecuteAsync<TRequest, TResponse>(
        this IRequestBatcher<TRequest, TResponse> batcher,
        IEnumerable<TRequest> requests,
        Delegate handler,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batcher);
        ArgumentNullException.ThrowIfNull(requests);
        ArgumentNullException.ThrowIfNull(handler);
        return GetDelegateBatcher(batcher).SubmitAsync(requests, handler, cancellationToken);
    }

    private static IDelegateRequestBatcher<TRequest> GetDelegateBatcher<TRequest>(IRequestBatcher<TRequest> batcher) =>
        batcher as IDelegateRequestBatcher<TRequest> ?? throw UnsupportedBatcher();

    private static IDelegateRequestBatcher<TRequest, TResponse> GetDelegateBatcher<TRequest, TResponse>(
        IRequestBatcher<TRequest, TResponse> batcher) =>
        batcher as IDelegateRequestBatcher<TRequest, TResponse> ?? throw UnsupportedBatcher();

    private static InvalidOperationException UnsupportedBatcher() => new(
        "Delegate execution requires a batcher registered with AddDelegateRequestBatcher.");
}
