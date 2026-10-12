namespace RequestBatcher.PendingRequests;

internal sealed class RequestCompletion()
    : TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously), IRequestCompletion
{
    public void CompleteSuccessfully() => TrySetResult();

    public void CompleteWithError(Exception exception) => TrySetException(exception);

    public void CompleteCanceled(CancellationToken cancellationToken) => TrySetCanceled(cancellationToken);
}
