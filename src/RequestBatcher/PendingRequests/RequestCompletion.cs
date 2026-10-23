namespace RequestBatcher.PendingRequests;

internal class RequestCompletion()
    : TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously), IRequestCompletion
{
    public virtual void CompleteSuccessfully() => TrySetResult();

    public virtual void CompleteWithError(Exception exception) => TrySetException(exception);

    public virtual void CompleteCanceled(CancellationToken cancellationToken) => TrySetCanceled(cancellationToken);
}
