using RequestBatcher.PendingRequests;

namespace RequestBatcher.Diagnostics.Tracing;

internal sealed class TracedRequestCompletion : RequestCompletion, IRequestCompletionNotification
{
    private readonly IRequestCompletionObserver _observer;
    private Exception? _error;
    private CancellationToken _cancellationToken;
    private bool _isCanceled;

    internal TracedRequestCompletion(IRequestCompletionObserver observer)
    {
        _observer = observer;
        observer.Register(this);
    }

    Exception? IRequestCompletionNotification.Error => _error;

    bool IRequestCompletionNotification.IsCanceled => _isCanceled;

    public override void CompleteSuccessfully() => _observer.OnCompleted(this);

    public override void CompleteWithError(Exception exception)
    {
        _error = exception;
        _observer.OnCompleted(this);
    }

    public override void CompleteCanceled(CancellationToken cancellationToken)
    {
        _cancellationToken = cancellationToken;
        _isCanceled = true;
        _observer.OnCompleted(this);
    }

    void IRequestCompletionNotification.Publish()
    {
        if (_error is not null)
        {
            base.CompleteWithError(_error);
        }
        else if (_isCanceled)
        {
            base.CompleteCanceled(_cancellationToken);
        }
        else
        {
            base.CompleteSuccessfully();
        }
    }
}
