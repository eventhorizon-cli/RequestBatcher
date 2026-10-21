using System.Diagnostics;

namespace RequestBatcher.PendingRequests;

// Keep tracing metadata off the ordinary pending item so disabled tracing does not enlarge every queued request.
internal sealed class TracedPendingBatchRequest<TRequest>(
    TRequest request,
    CancellationToken cancellationToken,
    Action<PendingBatchRequest<TRequest>> onFinished,
    IRequestCompletion? completion,
    ActivityContext context)
    : PendingBatchRequest<TRequest>(request, cancellationToken, onFinished, completion)
{
    public ActivityContext Context { get; } = context;
}
