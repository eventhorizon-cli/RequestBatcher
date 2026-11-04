using RequestBatcher.PendingRequests;

namespace RequestBatcher.Diagnostics.Tracing;

/// <summary>
/// Exposes a submission's final aggregate outcome to tracing before publishing its completion task.
/// </summary>
internal sealed class TracedBatchSubmissionCompletion : BatchSubmissionCompletion, IRequestCompletionNotification
{
    private readonly IRequestCompletionObserver _observer;

    internal TracedBatchSubmissionCompletion(int requestCount, CancellationToken cancellationToken, IRequestCompletionObserver observer)
        : base(requestCount, cancellationToken)
    {
        _observer = observer;
        // Reuse the aggregate state; tracing only intercepts its final publication.
        observer.Register(this);
    }

    Exception? IRequestCompletionNotification.Error => Error;

    bool IRequestCompletionNotification.IsCanceled => IsCanceled;

    // The shared aggregate intercepts only final publication after all submitted requests are terminal.
    protected override void Publish() => _observer.OnCompleted(this);

    void IRequestCompletionNotification.Publish() => base.Publish();
}
