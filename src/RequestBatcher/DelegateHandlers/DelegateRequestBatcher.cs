using System.Diagnostics.CodeAnalysis;
using RequestBatcher.Diagnostics;
using RequestBatcher.PendingRequests;

namespace RequestBatcher.DelegateHandlers;

internal sealed class DelegateRequestBatcher<TRequest>(
    RequestBatchCoordinator<TRequest> coordinator,
    DelegateRequestBatchHandler<TRequest> handler) : IRequestBatcher<TRequest>, IDelegateRequestBatcher<TRequest>
{
    private readonly RequestBatcherTraceMetadata _traceMetadata = new(
        RequestBatcherRequestType.GetName(typeof(TRequest)),
        coordinator.BatcherName);

    public Task ExecuteAsync(TRequest request, CancellationToken cancellationToken = default) =>
        throw MissingHandler();

    public Task ExecuteAsync(IEnumerable<TRequest> requests, CancellationToken cancellationToken = default) =>
        throw MissingHandler();

    [RequiresDynamicCode("Delegate handlers compile a typed invoker on first use.")]
    [RequiresUnreferencedCode("Delegate handlers inspect the delegate signature and service parameter attributes.")]
    public Task SubmitAsync(TRequest request, Delegate callback, CancellationToken cancellationToken) =>
        RequestBatcherTracing.Source.HasListeners()
            ? RequestBatcherTracing.TraceCall(
                _traceMetadata,
                (Batcher: this, Request: request, Callback: callback, Token: cancellationToken),
                static (state, context, observer) => state.Batcher.SubmitCoreAsync(
                    state.Request, state.Callback, state.Token, context, observer))
            : SubmitCoreAsync(request, callback, cancellationToken);

    [RequiresDynamicCode("Delegate handlers compile a typed invoker on first use.")]
    [RequiresUnreferencedCode("Delegate handlers inspect the delegate signature and service parameter attributes.")]
    private Task SubmitCoreAsync(
        TRequest request,
        Delegate callback,
        CancellationToken cancellationToken,
        SubmissionTraceContext? traceContext = default,
        IRequestCompletionObserver? completionObserver = null)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled(cancellationToken);
        }

        handler.Bind(callback);
        return coordinator.ExecuteCoreAsync(request, null, cancellationToken, traceContext, completionObserver);
    }

    [RequiresDynamicCode("Delegate handlers compile a typed invoker on first use.")]
    [RequiresUnreferencedCode("Delegate handlers inspect the delegate signature and service parameter attributes.")]
    public Task SubmitAsync(
        IEnumerable<TRequest> requests,
        Delegate callback,
        CancellationToken cancellationToken) =>
        RequestBatcherTracing.Source.HasListeners()
            ? RequestBatcherTracing.TraceCall(
                _traceMetadata,
                (Batcher: this, Request: requests, Callback: callback, Token: cancellationToken),
                static (state, context, observer) => state.Batcher.SubmitCoreAsync(
                    state.Request, state.Callback, state.Token, context, observer))
            : SubmitCoreAsync(requests, callback, cancellationToken);

    [RequiresDynamicCode("Delegate handlers compile a typed invoker on first use.")]
    [RequiresUnreferencedCode("Delegate handlers inspect the delegate signature and service parameter attributes.")]
    private Task SubmitCoreAsync(
        IEnumerable<TRequest> requests,
        Delegate callback,
        CancellationToken cancellationToken,
        SubmissionTraceContext? traceContext = default,
        IRequestCompletionObserver? completionObserver = null)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled(cancellationToken);
        }

        handler.Bind(callback);
        return coordinator.ExecuteCoreAsync(requests, cancellationToken, traceContext, completionObserver);
    }

    private static InvalidOperationException MissingHandler() => new(
        "This request batcher requires a delegate handler. Use the ExecuteAsync overload with a handler delegate.");
}
