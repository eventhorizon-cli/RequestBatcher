using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using RequestBatcher.Diagnostics;
using RequestBatcher.Diagnostics.Metrics;
using RequestBatcher.Diagnostics.Tracing;
using RequestBatcher.PendingRequests;

namespace RequestBatcher.Handlers.Delegate;

internal sealed class DelegateRequestBatcher<TRequest, TResponse>(
    [FromKeyedServices] ResponseRequestBatcher<TRequest, TResponse> batcher,
    [FromKeyedServices] DelegateRequestBatchHandler<RequestBatchEntry<TRequest, TResponse>> handler,
    RequestBatcherMetrics metrics,
    [FromKeyedServices] RequestBatcherConfiguration<RequestBatchEntry<TRequest, TResponse>> configuration)
    : IRequestBatcher<TRequest, TResponse>, IDelegateRequestBatcher<TRequest, TResponse>
{
    private readonly RequestBatcherMetadata _metadata = configuration.Metadata;

    public Task<TResponse> ExecuteAsync(TRequest request, CancellationToken cancellationToken = default) =>
        throw MissingHandler();

    public Task<IReadOnlyList<TResponse>> ExecuteAsync(
        IEnumerable<TRequest> requests,
        CancellationToken cancellationToken = default) => throw MissingHandler();

    [RequiresDynamicCode("Delegate handlers compile a typed invoker on first use.")]
    [RequiresUnreferencedCode("Delegate handlers inspect the delegate signature and service parameter attributes.")]
    public Task<TResponse> SubmitAsync(TRequest request, System.Delegate callback, CancellationToken cancellationToken) =>
        ExecuteCall(
            RequestBatcherMetricTags.SingleSubmission,
            (Batcher: this, Request: request, Callback: callback, Token: cancellationToken),
            static (state, context, observer) => state.Batcher.SubmitCoreAsync(
                state.Request, state.Callback, state.Token, context, observer));

    [RequiresDynamicCode("Delegate handlers compile a typed invoker on first use.")]
    [RequiresUnreferencedCode("Delegate handlers inspect the delegate signature and service parameter attributes.")]
    private Task<TResponse> SubmitCoreAsync(
        TRequest request,
        System.Delegate callback,
        CancellationToken cancellationToken,
        SubmissionTraceContext? traceContext = default,
        IRequestCompletionObserver? completionObserver = null)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<TResponse>(cancellationToken);
        }

        // Bind inside the public call boundary so binding failures are traced and measured exactly once.
        handler.Bind(callback);
        return batcher.ExecuteCoreAsync(request, cancellationToken, traceContext, completionObserver);
    }

    [RequiresDynamicCode("Delegate handlers compile a typed invoker on first use.")]
    [RequiresUnreferencedCode("Delegate handlers inspect the delegate signature and service parameter attributes.")]
    public Task<IReadOnlyList<TResponse>> SubmitAsync(
        IEnumerable<TRequest> requests,
        System.Delegate callback,
        CancellationToken cancellationToken) =>
        // Wait for response-array assembly, not just the underlying queue completion.
        ExecuteCall(
            RequestBatcherMetricTags.MultiSubmission,
            (Batcher: this, Requests: requests, Callback: callback, Token: cancellationToken),
            static (state, context, _) => state.Batcher.SubmitCoreAsync(
                state.Requests, state.Callback, state.Token, context),
            observeCompletion: false);

    [RequiresDynamicCode("Delegate handlers compile a typed invoker on first use.")]
    [RequiresUnreferencedCode("Delegate handlers inspect the delegate signature and service parameter attributes.")]
    private Task<IReadOnlyList<TResponse>> SubmitCoreAsync(
        IEnumerable<TRequest> requests,
        System.Delegate callback,
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

    private Task<TResult> ExecuteCall<TResult, TState>(
        string kind,
        TState state,
        Func<TState, SubmissionTraceContext?, IRequestCompletionObserver?, Task<TResult>> submit,
        bool observeCompletion = true)
    {
        return metrics.ExecuteEnabled
            ? metrics.MeasureExecution(
                _metadata.RequestTypeName,
                _metadata.BatcherName,
                kind,
                (Batcher: this, State: state, Submit: submit, ObserveCompletion: observeCompletion),
                static args => args.Batcher.ExecuteWithTracing(args.State, args.Submit, args.ObserveCompletion))
            : ExecuteWithTracing(state, submit, observeCompletion);
    }

    private Task<TResult> ExecuteWithTracing<TResult, TState>(
        TState state,
        Func<TState, SubmissionTraceContext?, IRequestCompletionObserver?, Task<TResult>> submit,
        bool observeCompletion) =>
        RequestBatcherTracing.Source.HasListeners()
            ? RequestBatcherTracing.TraceCall(_metadata, state, submit, observeCompletion)
            : submit(state, null, null);
}
