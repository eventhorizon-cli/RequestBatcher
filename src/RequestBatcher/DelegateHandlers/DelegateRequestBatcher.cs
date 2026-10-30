using System.Diagnostics.CodeAnalysis;
using RequestBatcher.Diagnostics;
using RequestBatcher.PendingRequests;

namespace RequestBatcher.DelegateHandlers;

internal sealed class DelegateRequestBatcher<TRequest>(
    RequestBatchCoordinator<TRequest> coordinator,
    DelegateRequestBatchHandler<TRequest> handler) : IRequestBatcher<TRequest>, IDelegateRequestBatcher<TRequest>
{
    private static readonly string _telemetryRequestTypeName = RequestBatcherRequestType.GetName(typeof(TRequest));
    private readonly RequestBatcherCallDiagnostics _callDiagnostics = new(
        _telemetryRequestTypeName,
        coordinator.BatcherName,
        coordinator.Metrics);

    public Task ExecuteAsync(TRequest request, CancellationToken cancellationToken = default) =>
        throw MissingHandler();

    public Task ExecuteAsync(IEnumerable<TRequest> requests, CancellationToken cancellationToken = default) =>
        throw MissingHandler();

    [RequiresDynamicCode("Delegate handlers compile a typed invoker on first use.")]
    [RequiresUnreferencedCode("Delegate handlers inspect the delegate signature and service parameter attributes.")]
    public Task SubmitAsync(TRequest request, Delegate callback, CancellationToken cancellationToken) =>
        _callDiagnostics.Execute(
            RequestBatcherMetricTags.SingleSubmission,
            (Batcher: this, Request: request, Callback: callback, Token: cancellationToken),
            static (state, context, observer) => state.Batcher.SubmitCoreAsync(
                state.Request, state.Callback, state.Token, context, observer));

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

        // Bind inside the shared boundary so binding failures are traced and measured; forward without wrapping again.
        handler.Bind(callback);
        return coordinator.ExecuteCoreAsync(request, null, cancellationToken, traceContext, completionObserver);
    }

    [RequiresDynamicCode("Delegate handlers compile a typed invoker on first use.")]
    [RequiresUnreferencedCode("Delegate handlers inspect the delegate signature and service parameter attributes.")]
    public Task SubmitAsync(
        IEnumerable<TRequest> requests,
        Delegate callback,
        CancellationToken cancellationToken) =>
        _callDiagnostics.Execute(
            RequestBatcherMetricTags.MultiSubmission,
            (Batcher: this, Requests: requests, Callback: callback, Token: cancellationToken),
            static (state, context, observer) => state.Batcher.SubmitCoreAsync(
                state.Requests, state.Callback, state.Token, context, observer));

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
