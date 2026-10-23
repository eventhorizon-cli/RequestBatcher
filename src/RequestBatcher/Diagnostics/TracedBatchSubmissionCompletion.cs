using RequestBatcher.PendingRequests;

namespace RequestBatcher.Diagnostics;

internal sealed class TracedBatchSubmissionCompletion : BatchSubmissionCompletion, IRequestCompletionNotification
{
    private readonly IRequestCompletionObserver _observer;

    internal TracedBatchSubmissionCompletion(int requestCount, CancellationToken cancellationToken, IRequestCompletionObserver observer)
        : base(requestCount, cancellationToken)
    {
        _observer = observer;
        observer.Register(this);
    }

    Exception? IRequestCompletionNotification.Error => Error;

    bool IRequestCompletionNotification.IsCanceled => IsCanceled;

    protected override void Publish() => _observer.OnCompleted(this);

    void IRequestCompletionNotification.Publish() => base.Publish();
}
