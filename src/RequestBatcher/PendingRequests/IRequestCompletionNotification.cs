namespace RequestBatcher.PendingRequests;

/// <summary>
/// A completion object exposing its stored outcome for publication after the enclosing call boundary is known.
/// </summary>
internal interface IRequestCompletionNotification
{
    /// <summary>
    /// Gets the task that will be completed when this notification is published.
    /// </summary>
    Task Task { get; }

    /// <summary>
    /// Gets the terminal error, if any.
    /// </summary>
    Exception? Error { get; }

    /// <summary>
    /// Gets whether the terminal outcome is cancellation without an error.
    /// </summary>
    bool IsCanceled { get; }

    /// <summary>
    /// Publishes the stored terminal outcome to the completion task.
    /// </summary>
    void Publish();
}
