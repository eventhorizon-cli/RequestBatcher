namespace RequestBatcher.PendingRequests;

internal interface IRequestCompletionNotification
{
    Task Task { get; }

    Exception? Error { get; }

    bool IsCanceled { get; }

    void Publish();
}
