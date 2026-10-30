using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using RequestBatcher.Diagnostics;
using RequestBatcher.Diagnostics.Metrics;
using RequestBatcher.Diagnostics.Tracing;
using RequestBatcher.PendingRequests;

namespace RequestBatcher.Handlers.Delegate;

internal sealed class DelegateRequestBatcher<TRequest>(
    [FromKeyedServices] RequestBatchCoordinator<TRequest> coordinator,
    [FromKeyedServices] DelegateRequestBatchHandler<TRequest> handler,
    RequestBatcherMetrics metrics,
    [FromKeyedServices] RequestBatcherConfiguration<TRequest> configuration)
    : IRequestBatcher<TRequest>, IDelegateRequestBatcher<TRequest>
{
    private readonly RequestBatcherMetadata _metadata = configuration.Metadata;

    public Task ExecuteAsync(TRequest request, CancellationToken cancellationToken = default) =>
        throw MissingHandler();

    public Task ExecuteAsync(IEnumerable<TRequest> requests, CancellationToken cancellationToken = default) =>
        throw MissingHandler();

    [RequiresDynamicCode("Delegate handlers compile a typed invoker on first use.")]
    [RequiresUnreferencedCode("Delegate handlers inspect the delegate signature and service parameter attributes.")]
    public Task SubmitAsync(TRequest request, System.Delegate callback, CancellationToken cancellationToken) =>
        ExecuteCall(
            RequestBatcherMetricTags.SingleSubmission,
            (Batcher: this, Request: request, Callback: callback, Token: cancellationToken),
            static (state, context, observer) => state.Batcher.SubmitCoreAsync(
                state.Request, state.Callback, state.Token, context, observer));

    [RequiresDynamicCode("Delegate handlers compile a typed invoker on first use.")]
    [RequiresUnreferencedCode("Delegate handlers inspect the delegate signature and service parameter attributes.")]
    private Task SubmitCoreAsync(
        TRequest request,
        System.Delegate callback,
        CancellationToken cancellationToken,
        SubmissionTraceContext? traceContext = default,
        IRequestCompletionObserver? completionObserver = null)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled(cancellationToken);
        }

        // Bind inside the public call boundary so binding failures are traced and measured exactly once.
        handler.Bind(callback);
        return coordinator.ExecuteCoreAsync(request, null, cancellationToken, traceContext, completionObserver);
    }

    [RequiresDynamicCode("Delegate handlers compile a typed invoker on first use.")]
    [RequiresUnreferencedCode("Delegate handlers inspect the delegate signature and service parameter attributes.")]
    public Task SubmitAsync(
        IEnumerable<TRequest> requests,
        System.Delegate callback,
        CancellationToken cancellationToken) =>
        ExecuteCall(
            RequestBatcherMetricTags.MultiSubmission,
            (Batcher: this, Requests: requests, Callback: callback, Token: cancellationToken),
            static (state, context, observer) => state.Batcher.SubmitCoreAsync(
                state.Requests, state.Callback, state.Token, context, observer));

    [RequiresDynamicCode("Delegate handlers compile a typed invoker on first use.")]
    [RequiresUnreferencedCode("Delegate handlers inspect the delegate signature and service parameter attributes.")]
    private Task SubmitCoreAsync(
        IEnumerable<TRequest> requests,
        System.Delegate callback,
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

    private Task ExecuteCall<TState>(
        string kind,
        TState state,
        Func<TState, SubmissionTraceContext?, IRequestCompletionObserver?, Task> submit)
    {
        return metrics.ExecuteEnabled
            ? metrics.MeasureExecution(
                _metadata.RequestTypeName,
                _metadata.BatcherName,
                kind,
                (Batcher: this, State: state, Submit: submit),
                static args => args.Batcher.ExecuteWithTracing(args.State, args.Submit))
            : ExecuteWithTracing(state, submit);
    }

    private Task ExecuteWithTracing<TState>(
        TState state,
        Func<TState, SubmissionTraceContext?, IRequestCompletionObserver?, Task> submit) =>
        RequestBatcherTracing.Source.HasListeners()
            ? RequestBatcherTracing.TraceCall(_metadata, state, submit)
            : submit(state, null, null);
}
