namespace RequestBatcher.Internal;

internal sealed class BatchSubmissionCompletion
{
    private readonly CancellationToken _cancellationToken;
    private readonly object _exceptionsLock = new();
    private readonly TaskCompletionSource _source =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private HashSet<Exception>? _exceptions;
    private int _remainingRequests;
    private int _wasCanceled;

    public BatchSubmissionCompletion(
        int requestCount,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(requestCount);

        _remainingRequests = requestCount;
        _cancellationToken = cancellationToken;
    }

    public Task Task => _source.Task;

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
            _source.TrySetException(exceptions);
        }
        else if (Volatile.Read(ref _wasCanceled) != 0)
        {
            _source.TrySetCanceled(_cancellationToken);
        }
        else
        {
            _source.TrySetResult();
        }
    }
}
