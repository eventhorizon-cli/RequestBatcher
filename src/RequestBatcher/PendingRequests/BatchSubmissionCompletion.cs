namespace RequestBatcher.PendingRequests;

internal sealed class BatchSubmissionCompletion : TaskCompletionSource, IRequestCompletion
{
    private readonly CancellationToken _cancellationToken;
    private readonly object _exceptionsLock = new();
    private HashSet<Exception>? _exceptions;
    private int _remainingRequests;
    private int _wasCanceled;

    public BatchSubmissionCompletion(
        int requestCount,
        CancellationToken cancellationToken)
        : base(TaskCreationOptions.RunContinuationsAsynchronously)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(requestCount);

        _remainingRequests = requestCount;
        _cancellationToken = cancellationToken;
    }

    public void CompleteSuccessfully() => CompleteRequest();

    public void CompleteWithError(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        lock (_exceptionsLock)
        {
            _exceptions ??= new HashSet<Exception>(ReferenceEqualityComparer.Instance);
            _exceptions.Add(exception);
        }

        CompleteRequest();
    }

    public void CompleteCanceled()
    {
        Volatile.Write(ref _wasCanceled, 1);
        CompleteRequest();
    }

    void IRequestCompletion.CompleteCanceled(CancellationToken cancellationToken) => CompleteCanceled();

    private void CompleteRequest()
    {
        var remainingRequests = Interlocked.Decrement(ref _remainingRequests);
        if (remainingRequests != 0)
        {
            return;
        }

        HashSet<Exception>? exceptions;
        lock (_exceptionsLock)
        {
            exceptions = _exceptions;
        }

        if (exceptions is not null)
        {
            TrySetException(exceptions);
        }
        else if (Volatile.Read(ref _wasCanceled) != 0)
        {
            TrySetCanceled(_cancellationToken);
        }
        else
        {
            TrySetResult();
        }
    }
}
