namespace RequestBatcher.PendingRequests;

/// <summary>
/// Coordinates deferred publication of a completion with its enclosing submission call.
/// </summary>
internal interface IRequestCompletionObserver
{
    /// <summary>
    /// Registers the completion before the submission call returns.
    /// </summary>
    void Register(IRequestCompletionNotification completion);

    /// <summary>
    /// Notifies the observer that the registered completion has reached a terminal outcome.
    /// </summary>
    void OnCompleted(IRequestCompletionNotification completion);
}
