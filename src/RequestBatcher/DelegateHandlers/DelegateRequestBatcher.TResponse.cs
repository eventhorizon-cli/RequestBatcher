using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using RequestBatcher.Diagnostics;

namespace RequestBatcher.DelegateHandlers;

internal sealed class DelegateRequestBatcher<TRequest, TResponse>(
    ResponseRequestBatcher<TRequest, TResponse> batcher,
    DelegateRequestBatchHandler<RequestBatchEntry<TRequest, TResponse>> handler)
    : IRequestBatcher<TRequest, TResponse>, IDelegateRequestBatcher<TRequest, TResponse>
{
    private static readonly string _requestTypeName = typeof(TRequest).FullName ?? typeof(TRequest).Name;

    public Task<TResponse> ExecuteAsync(TRequest request, CancellationToken cancellationToken = default) =>
        throw MissingHandler();

    public Task<IReadOnlyList<TResponse>> ExecuteAsync(
        IEnumerable<TRequest> requests,
        CancellationToken cancellationToken = default) => throw MissingHandler();

    [RequiresDynamicCode("Delegate handlers compile a typed invoker on first use.")]
    [RequiresUnreferencedCode("Delegate handlers inspect the delegate signature and service parameter attributes.")]
    public Task<TResponse> SubmitAsync(TRequest request, Delegate callback, CancellationToken cancellationToken) =>
        RequestBatcherTracing.Source.HasListeners()
            ? RequestBatcherTracing.TraceCall(
                _requestTypeName,
                (Batcher: this, Request: request, Callback: callback, Token: cancellationToken),
                static (state, context) => state.Batcher.SubmitCoreAsync(
                    state.Request, state.Callback, state.Token, context),
                batcher.BatcherName)
            : SubmitCoreAsync(request, callback, cancellationToken);

    [RequiresDynamicCode("Delegate handlers compile a typed invoker on first use.")]
    [RequiresUnreferencedCode("Delegate handlers inspect the delegate signature and service parameter attributes.")]
    private Task<TResponse> SubmitCoreAsync(
        TRequest request,
        Delegate callback,
        CancellationToken cancellationToken,
        ActivityContext traceContext = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<TResponse>(cancellationToken);
        }

        handler.Bind(callback);
        return batcher.ExecuteCoreAsync(request, cancellationToken, traceContext);
    }

    [RequiresDynamicCode("Delegate handlers compile a typed invoker on first use.")]
    [RequiresUnreferencedCode("Delegate handlers inspect the delegate signature and service parameter attributes.")]
    public Task<IReadOnlyList<TResponse>> SubmitAsync(
        IEnumerable<TRequest> requests,
        Delegate callback,
        CancellationToken cancellationToken) =>
        RequestBatcherTracing.Source.HasListeners()
            ? RequestBatcherTracing.TraceCall(
                _requestTypeName,
                (Batcher: this, Request: requests, Callback: callback, Token: cancellationToken),
                static (state, context) => state.Batcher.SubmitCoreAsync(
                    state.Request, state.Callback, state.Token, context),
                batcher.BatcherName)
            : SubmitCoreAsync(requests, callback, cancellationToken);

    [RequiresDynamicCode("Delegate handlers compile a typed invoker on first use.")]
    [RequiresUnreferencedCode("Delegate handlers inspect the delegate signature and service parameter attributes.")]
    private Task<IReadOnlyList<TResponse>> SubmitCoreAsync(
        IEnumerable<TRequest> requests,
        Delegate callback,
        CancellationToken cancellationToken,
        ActivityContext traceContext = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<IReadOnlyList<TResponse>>(cancellationToken);
        }

        handler.Bind(callback);
        return batcher.ExecuteCoreAsync(requests, cancellationToken, traceContext);
    }

    private static InvalidOperationException MissingHandler() => new(
        "This request batcher requires a delegate handler. Use the ExecuteAsync overload with a handler delegate.");
}
