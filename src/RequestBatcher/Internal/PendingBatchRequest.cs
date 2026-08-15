namespace RequestBatcher.Internal;

internal sealed class PendingBatchRequest<TRequest>
{
    private const int Queued = 0;
    private const int Processing = 1;
    private const int Canceled = 2;
    private const int Completed = 3;

    private readonly TaskCompletionSource _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Action<PendingBatchRequest<TRequest>> _onFinished;
    private readonly CancellationToken _cancellationToken;
    private CancellationTokenRegistration _cancellationRegistration;
    private int _status;
    private int _finished;

    public PendingBatchRequest(
        TRequest request,
        CancellationToken cancellationToken,
        Action<PendingBatchRequest<TRequest>> onFinished)
    {
        Request = request;
        _cancellationToken = cancellationToken;
        _onFinished = onFinished;

        if (cancellationToken.CanBeCanceled)
        {
            _cancellationRegistration = cancellationToken.UnsafeRegister(
                static state => ((PendingBatchRequest<TRequest>)state!).CancelWhileQueued(),
                this);
        }
    }

    public TRequest Request { get; }

    public Task Completion => _completion.Task;

    public bool TryStartProcessing() =>
        Interlocked.CompareExchange(ref _status, Processing, Queued) == Queued;

    public void CompleteSuccessfully()
    {
        if (Interlocked.CompareExchange(ref _status, Completed, Processing) == Processing)
        {
            _completion.TrySetResult();
        }

        Finish();
    }

    public void CompleteWithError(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (Interlocked.CompareExchange(ref _status, Completed, Processing) == Processing)
        {
            _completion.TrySetException(exception);
        }

        Finish();
    }

    public void FinishCanceledRequest() => Finish();

    public void FailWhileQueued(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (Interlocked.CompareExchange(ref _status, Completed, Queued) == Queued)
        {
            _completion.TrySetException(exception);
        }

        Finish();
    }

    private void CancelWhileQueued()
    {
        if (Interlocked.CompareExchange(ref _status, Canceled, Queued) == Queued)
        {
            _completion.TrySetCanceled(_cancellationToken);
        }
    }

    private void Finish()
    {
        if (Interlocked.Exchange(ref _finished, 1) != 0)
        {
            return;
        }

        _cancellationRegistration.Dispose();
        _onFinished(this);
    }
}
