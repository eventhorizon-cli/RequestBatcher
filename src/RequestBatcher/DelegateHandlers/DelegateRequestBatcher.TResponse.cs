using System.Diagnostics.CodeAnalysis;

namespace RequestBatcher.DelegateHandlers;

internal sealed class DelegateRequestBatcher<TRequest, TResponse>(
    ResponseRequestBatcher<TRequest, TResponse> batcher,
    DelegateRequestBatchHandler<RequestBatchEntry<TRequest, TResponse>> handler)
    : IRequestBatcher<TRequest, TResponse>, IDelegateRequestBatcher<TRequest, TResponse>
{
    public Task<TResponse> ExecuteAsync(TRequest request, CancellationToken cancellationToken = default) =>
        throw MissingHandler();

    public Task<IReadOnlyList<TResponse>> ExecuteAsync(
        IEnumerable<TRequest> requests,
        CancellationToken cancellationToken = default) => throw MissingHandler();

    [RequiresDynamicCode("Delegate handlers compile a typed invoker on first use.")]
    [RequiresUnreferencedCode("Delegate handlers inspect the delegate signature and service parameter attributes.")]
    public Task<TResponse> SubmitAsync(TRequest request, Delegate callback, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<TResponse>(cancellationToken);
        }

        handler.Bind(callback);
        return batcher.ExecuteAsync(request, cancellationToken);
    }

    [RequiresDynamicCode("Delegate handlers compile a typed invoker on first use.")]
    [RequiresUnreferencedCode("Delegate handlers inspect the delegate signature and service parameter attributes.")]
    public Task<IReadOnlyList<TResponse>> SubmitAsync(
        IEnumerable<TRequest> requests,
        Delegate callback,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<IReadOnlyList<TResponse>>(cancellationToken);
        }

        handler.Bind(callback);
        return batcher.ExecuteAsync(requests, cancellationToken);
    }

    private static InvalidOperationException MissingHandler() => new(
        "This request batcher requires a delegate handler. Use the ExecuteAsync overload with a handler delegate.");
}
