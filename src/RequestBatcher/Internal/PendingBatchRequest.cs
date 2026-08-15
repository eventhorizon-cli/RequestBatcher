namespace RequestBatcher.Internal;

internal sealed class PendingBatchRequest<TRequest>
{
    private const int Queued = 0;
    private const int Processing = 1;
    private const int Canceled = 2;
    private const int Completed = 3;

    // These states are mutually exclusive; one field keeps single requests from growing in size.
    private readonly object _completion;
    private readonly Action<PendingBatchRequest<TRequest>> _onFinished;
    private readonly CancellationToken _cancellationToken;
    private CancellationTokenRegistration _cancellationRegistration;
    private int _status;
    private int _finished;

    public PendingBatchRequest(
        TRequest request,
        CancellationToken cancellationToken,
        Action<PendingBatchRequest<TRequest>> onFinished,
        BatchSubmissionCompletion? batchCompletion = null)
    {
        Request = request;
        _cancellationToken = cancellationToken;
        _onFinished = onFinished;
        _completion = batchCompletion is null
            ? new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
            : batchCompletion;

        if (cancellationToken.CanBeCanceled)
        {
            _cancellationRegistration = cancellationToken.UnsafeRegister(
                static state => ((PendingBatchRequest<TRequest>)state!).CancelWhileQueued(),
                this);
        }
    }

    public TRequest Request { get; }

    public Task Completion => _completion is BatchSubmissionCompletion batchCompletion
        ? batchCompletion.Task
        : ((TaskCompletionSource)_completion).Task;

    public bool TryStartProcessing() =>
        Interlocked.CompareExchange(ref _status, Processing, Queued) == Queued;

    public void CompleteSuccessfully()
    {
        if (Interlocked.CompareExchange(ref _status, Completed, Processing) == Processing)
        {
            CompleteSuccessfullyCore();
        }

        Finish();
    }

    public void CompleteWithError(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (Interlocked.CompareExchange(ref _status, Completed, Processing) == Processing)
        {
            CompleteWithErrorCore(exception);
        }

        Finish();
    }

    public void FinishCanceledRequest() => Finish();

    public void FailWhileQueued(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (Interlocked.CompareExchange(ref _status, Completed, Queued) == Queued)
        {
            CompleteWithErrorCore(exception);
        }

        Finish();
    }

    private void CancelWhileQueued()
    {
        if (Interlocked.CompareExchange(ref _status, Canceled, Queued) == Queued)
        {
            CompleteCanceledCore();
        }
    }

    private void CompleteSuccessfullyCore()
    {
        if (_completion is BatchSubmissionCompletion batchCompletion)
        {
            batchCompletion.CompleteSuccessfully();
        }
        else
        {
            ((TaskCompletionSource)_completion).TrySetResult();
        }
    }

    private void CompleteWithErrorCore(Exception exception)
    {
        if (_completion is BatchSubmissionCompletion batchCompletion)
        {
            batchCompletion.CompleteWithError(exception);
        }
        else
        {
            ((TaskCompletionSource)_completion).TrySetException(exception);
        }
    }

    private void CompleteCanceledCore()
    {
        if (_completion is BatchSubmissionCompletion batchCompletion)
        {
            batchCompletion.CompleteCanceled();
        }
        else
        {
            ((TaskCompletionSource)_completion).TrySetCanceled(_cancellationToken);
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
