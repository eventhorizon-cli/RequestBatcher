namespace RequestBatcher.PendingRequests;

internal interface IRequestCompletion
{
    Task Task { get; }

    void CompleteSuccessfully();

    void CompleteWithError(Exception exception);

    void CompleteCanceled(CancellationToken cancellationToken);
}
