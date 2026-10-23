namespace RequestBatcher.PendingRequests;

internal interface IRequestCompletionObserver
{
    void Register(IRequestCompletionNotification completion);

    void OnCompleted(IRequestCompletionNotification completion);
}
