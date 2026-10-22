using RequestBatcher.Diagnostics;

namespace RequestBatcher.PendingRequests;

// Keep tracing metadata off the ordinary pending item so disabled tracing does not enlarge every queued request.
internal sealed class TracedPendingBatchRequest<TRequest>(
    TRequest request,
    CancellationToken cancellationToken,
    Action<PendingBatchRequest<TRequest>> onFinished,
    IRequestCompletion? completion,
    SubmissionTraceContext traceContext)
    : PendingBatchRequest<TRequest>(request, cancellationToken, onFinished, completion)
{
    public SubmissionTraceContext TraceContext { get; } = traceContext;
}
