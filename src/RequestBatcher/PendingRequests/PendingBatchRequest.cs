namespace RequestBatcher.PendingRequests;

internal sealed class PendingBatchRequest<TRequest>
{
    private const int Queued = 0;
    private const int Processing = 1;
    private const int Canceled = 2;
    private const int Completed = 3;

    private readonly IRequestCompletion _completion;
    private readonly Action<PendingBatchRequest<TRequest>> _onFinished;
    private CancellationTokenRegistration _cancellationRegistration;
    private int _status;
    private int _finished;

    public PendingBatchRequest(
        TRequest request,
        CancellationToken cancellationToken,
        Action<PendingBatchRequest<TRequest>> onFinished,
        IRequestCompletion? completion = null)
    {
        Request = request;
        _onFinished = onFinished;
        _completion = completion ?? new RequestCompletion();

        if (cancellationToken.CanBeCanceled)
        {
            _cancellationRegistration = cancellationToken.UnsafeRegister(
                static (state, originalToken) =>
                    ((PendingBatchRequest<TRequest>)state!).CancelWhileQueued(originalToken),
                this);
        }
    }

    public TRequest Request { get; }

    public Task Completion => _completion is BatchSubmissionCompletion batchCompletion
        ? batchCompletion.Task
        : _completion.Task;

    public bool TryStartProcessing() =>
        Interlocked.CompareExchange(ref _status, Processing, Queued) == Queued;

    public void CompleteSuccessfully()
    {
        if (Interlocked.CompareExchange(ref _status, Completed, Processing) == Processing)
        {
            if (_completion is BatchSubmissionCompletion batchCompletion)
            {
                batchCompletion.CompleteSuccessfully();
            }
            else
            {
                _completion.CompleteSuccessfully();
            }
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

        // An oversized batch can have earlier slices in the handler when production of a later slice fails.
        var status = Interlocked.CompareExchange(ref _status, Completed, Queued);
        if (status == Queued)
        {
            CompleteWithErrorCore(exception);
            Finish();
        }
        else if (status != Processing)
        {
            Finish();
        }
    }

    private void CancelWhileQueued(CancellationToken originalToken)
    {
        if (Interlocked.CompareExchange(ref _status, Canceled, Queued) == Queued)
        {
            if (_completion is BatchSubmissionCompletion batchCompletion)
            {
                batchCompletion.CompleteCanceled();
            }
            else
            {
                _completion.CompleteCanceled(originalToken);
            }
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
            _completion.CompleteWithError(exception);
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
