namespace RequestBatcher.PendingRequests;

internal sealed class ResponseCompletion<TRequest, TResponse>(RequestBatchItem<TRequest, TResponse> entry)
    : TaskCompletionSource<TResponse>(TaskCreationOptions.RunContinuationsAsynchronously), IRequestCompletion
{
    Task IRequestCompletion.Task => Task;

    public void CompleteSuccessfully() => TrySetResult(entry.GetResponse());

    public void CompleteWithError(Exception exception)
    {
        // Preserve canceled status and token; TCS supplies the standard cancellation exception.
        if (exception is OperationCanceledException canceled)
        {
            TrySetCanceled(canceled.CancellationToken);
        }
        else
        {
            TrySetException(exception);
        }
    }

    public void CompleteCanceled(CancellationToken cancellationToken) => TrySetCanceled(cancellationToken);
}
