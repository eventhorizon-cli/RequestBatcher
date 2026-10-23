namespace RequestBatcher.PendingRequests;

internal class ResponseCompletion<TRequest, TResponse>(RequestBatchEntry<TRequest, TResponse> entry)
    : TaskCompletionSource<TResponse>(TaskCreationOptions.RunContinuationsAsynchronously), IRequestCompletion
{
    Task IRequestCompletion.Task => Task;

    public void CompleteSuccessfully() => PublishResult(entry.GetResponse());

    public void CompleteWithError(Exception exception)
    {
        // Preserve canceled status and token; TCS supplies the standard cancellation exception.
        if (exception is OperationCanceledException canceled)
        {
            PublishCanceled(canceled.CancellationToken);
        }
        else
        {
            PublishError(exception);
        }
    }

    public void CompleteCanceled(CancellationToken cancellationToken) => PublishCanceled(cancellationToken);

    protected virtual void PublishResult(TResponse result) => TrySetResult(result);

    protected virtual void PublishError(Exception exception) => TrySetException(exception);

    protected virtual void PublishCanceled(CancellationToken cancellationToken) => TrySetCanceled(cancellationToken);
}
