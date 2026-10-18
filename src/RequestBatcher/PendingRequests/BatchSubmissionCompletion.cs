namespace RequestBatcher.PendingRequests;

internal sealed class BatchSubmissionCompletion : TaskCompletionSource, IRequestCompletion
{
    private readonly CancellationToken _cancellationToken;
    private Exception? _firstException;
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

        var firstException = Volatile.Read(ref _firstException);
        if (firstException is null)
        {
            firstException = Interlocked.CompareExchange(ref _firstException, exception, null);
        }

        if (firstException is not null && !ReferenceEquals(firstException, exception))
        {
            var exceptions = Volatile.Read(ref _exceptions);
            if (exceptions is null)
            {
                var created = new HashSet<Exception>(ReferenceEqualityComparer.Instance) { firstException };
                exceptions = Interlocked.CompareExchange(ref _exceptions, created, null) ?? created;
            }

            lock (exceptions)
            {
                exceptions.Add(exception);
            }
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

        // Error writers publish before decrementing, so the last item owns a stable exception set.
        if (Volatile.Read(ref _exceptions) is { } exceptions)
        {
            TrySetException(exceptions);
        }
        else if (Volatile.Read(ref _firstException) is { } firstException)
        {
            TrySetException(firstException);
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
