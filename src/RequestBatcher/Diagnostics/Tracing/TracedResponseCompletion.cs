using RequestBatcher.PendingRequests;

namespace RequestBatcher.Diagnostics.Tracing;

internal sealed class TracedResponseCompletion<TRequest, TResponse> : ResponseCompletion<TRequest, TResponse>, IRequestCompletionNotification
{
    private readonly IRequestCompletionObserver _observer;
    private TResponse _result = default!;
    private Exception? _error;
    private CancellationToken _cancellationToken;
    private bool _isCanceled;

    internal TracedResponseCompletion(RequestBatchEntry<TRequest, TResponse> entry, IRequestCompletionObserver observer)
        : base(entry)
    {
        _observer = observer;
        observer.Register(this);
    }

    Task IRequestCompletionNotification.Task => Task;

    Exception? IRequestCompletionNotification.Error => _error;

    bool IRequestCompletionNotification.IsCanceled => _isCanceled;

    protected override void PublishResult(TResponse result)
    {
        _result = result;
        _observer.OnCompleted(this);
    }

    protected override void PublishError(Exception exception)
    {
        _error = exception;
        _observer.OnCompleted(this);
    }

    protected override void PublishCanceled(CancellationToken cancellationToken)
    {
        _cancellationToken = cancellationToken;
        _isCanceled = true;
        _observer.OnCompleted(this);
    }

    void IRequestCompletionNotification.Publish()
    {
        if (_error is not null)
        {
            base.PublishError(_error);
        }
        else if (_isCanceled)
        {
            base.PublishCanceled(_cancellationToken);
        }
        else
        {
            base.PublishResult(_result);
        }
    }
}
