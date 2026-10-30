using System.Diagnostics.CodeAnalysis;
using RequestBatcher.Diagnostics;
using RequestBatcher.PendingRequests;

namespace RequestBatcher.DelegateHandlers;

internal sealed class DelegateRequestBatcher<TRequest, TResponse>(
    ResponseRequestBatcher<TRequest, TResponse> batcher,
    DelegateRequestBatchHandler<RequestBatchEntry<TRequest, TResponse>> handler)
    : IRequestBatcher<TRequest, TResponse>, IDelegateRequestBatcher<TRequest, TResponse>
{
    private static readonly string _telemetryRequestTypeName = RequestBatcherRequestType.GetName(typeof(TRequest));
    private readonly RequestBatcherCallDiagnostics _callDiagnostics = new(
        _telemetryRequestTypeName,
        batcher.BatcherName,
        batcher.Metrics);

    public Task<TResponse> ExecuteAsync(TRequest request, CancellationToken cancellationToken = default) =>
        throw MissingHandler();

    public Task<IReadOnlyList<TResponse>> ExecuteAsync(
        IEnumerable<TRequest> requests,
        CancellationToken cancellationToken = default) => throw MissingHandler();

    [RequiresDynamicCode("Delegate handlers compile a typed invoker on first use.")]
    [RequiresUnreferencedCode("Delegate handlers inspect the delegate signature and service parameter attributes.")]
    public Task<TResponse> SubmitAsync(TRequest request, Delegate callback, CancellationToken cancellationToken) =>
        _callDiagnostics.Execute(
            RequestBatcherMetricTags.SingleSubmission,
            (Batcher: this, Request: request, Callback: callback, Token: cancellationToken),
            static (state, context, observer) => state.Batcher.SubmitCoreAsync(
                state.Request, state.Callback, state.Token, context, observer));

    [RequiresDynamicCode("Delegate handlers compile a typed invoker on first use.")]
    [RequiresUnreferencedCode("Delegate handlers inspect the delegate signature and service parameter attributes.")]
    private Task<TResponse> SubmitCoreAsync(
        TRequest request,
        Delegate callback,
        CancellationToken cancellationToken,
        SubmissionTraceContext? traceContext = default,
        IRequestCompletionObserver? completionObserver = null)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<TResponse>(cancellationToken);
        }

        // Bind inside the shared boundary so binding failures are traced and measured; forward without wrapping again.
        handler.Bind(callback);
        return batcher.ExecuteCoreAsync(request, cancellationToken, traceContext, completionObserver);
    }

    [RequiresDynamicCode("Delegate handlers compile a typed invoker on first use.")]
    [RequiresUnreferencedCode("Delegate handlers inspect the delegate signature and service parameter attributes.")]
    public Task<IReadOnlyList<TResponse>> SubmitAsync(
        IEnumerable<TRequest> requests,
        Delegate callback,
        CancellationToken cancellationToken) =>
        // Wait for response-array assembly, not just the underlying queue completion.
        _callDiagnostics.Execute(
            RequestBatcherMetricTags.MultiSubmission,
            (Batcher: this, Requests: requests, Callback: callback, Token: cancellationToken),
            static (state, context, _) => state.Batcher.SubmitCoreAsync(
                state.Requests, state.Callback, state.Token, context),
            observeCompletion: false);

    [RequiresDynamicCode("Delegate handlers compile a typed invoker on first use.")]
    [RequiresUnreferencedCode("Delegate handlers inspect the delegate signature and service parameter attributes.")]
    private Task<IReadOnlyList<TResponse>> SubmitCoreAsync(
        IEnumerable<TRequest> requests,
        Delegate callback,
        CancellationToken cancellationToken,
        SubmissionTraceContext? traceContext = default)
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
