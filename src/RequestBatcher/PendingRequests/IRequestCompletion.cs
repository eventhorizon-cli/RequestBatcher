namespace RequestBatcher.PendingRequests;

/// <summary>
/// Receives terminal request outcomes and exposes the associated completion task.
/// </summary>
internal interface IRequestCompletion
{
    /// <summary>
    /// Gets the task representing this completion.
    /// </summary>
    Task Task { get; }

    /// <summary>
    /// Reports that one request completed successfully.
    /// </summary>
    void CompleteSuccessfully();

    /// <summary>
    /// Reports a handler or submission failure for one request.
    /// </summary>
    void CompleteWithError(Exception exception);

    /// <summary>
    /// Reports that one request was canceled.
    /// </summary>
    void CompleteCanceled(CancellationToken cancellationToken);
}
