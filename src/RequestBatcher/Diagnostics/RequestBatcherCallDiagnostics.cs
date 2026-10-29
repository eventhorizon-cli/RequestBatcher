using RequestBatcher.PendingRequests;

namespace RequestBatcher.Diagnostics;

// One metrics/tracing boundary for ordinary and delegate calls; internal forwarding uses uninstrumented cores.
internal sealed class RequestBatcherCallDiagnostics(string requestTypeName, string? batcherName)
{
    private readonly RequestBatcherTraceMetadata _traceMetadata = new(requestTypeName, batcherName);

    internal Task Execute<TState>(
        string kind,
        TState state,
        Func<TState, SubmissionTraceContext?, IRequestCompletionObserver?, Task> submit)
    {
        return RequestBatcherMetrics.ExecuteEnabled
            ? RequestBatcherMetrics.MeasureExecution(
                requestTypeName, batcherName, kind,
                (Diagnostics: this, State: state, Submit: submit),
                static args => args.Diagnostics.ExecuteWithTracing(args.State, args.Submit))
            : ExecuteWithTracing(state, submit);
    }

    internal Task<TResult> Execute<TState, TResult>(
        string kind,
        TState state,
        Func<TState, SubmissionTraceContext?, IRequestCompletionObserver?, Task<TResult>> submit,
        bool observeCompletion = true)
    {
        return RequestBatcherMetrics.ExecuteEnabled
            ? RequestBatcherMetrics.MeasureExecution(
                requestTypeName, batcherName, kind,
                (Diagnostics: this, State: state, Submit: submit, ObserveCompletion: observeCompletion),
                static args => args.Diagnostics.ExecuteWithTracing(args.State, args.Submit, args.ObserveCompletion))
            : ExecuteWithTracing(state, submit, observeCompletion);
    }

    private Task ExecuteWithTracing<TState>(
        TState state,
        Func<TState, SubmissionTraceContext?, IRequestCompletionObserver?, Task> submit) =>
        RequestBatcherTracing.Source.HasListeners()
            ? RequestBatcherTracing.TraceCall(_traceMetadata, state, submit)
            : submit(state, null, null);

    private Task<TResult> ExecuteWithTracing<TState, TResult>(
        TState state,
        Func<TState, SubmissionTraceContext?, IRequestCompletionObserver?, Task<TResult>> submit,
        bool observeCompletion) =>
        RequestBatcherTracing.Source.HasListeners()
            ? RequestBatcherTracing.TraceCall(_traceMetadata, state, submit, observeCompletion)
            : submit(state, null, null);
}
