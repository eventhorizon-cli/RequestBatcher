namespace RequestBatcher.Diagnostics.Tracing;

/// <summary>
/// Defines attribute keys for Execute and Handle spans.
/// </summary>
/// <remarks>
/// Library-specific attributes use the requestbatcher namespace; standard attributes retain their OpenTelemetry names.
/// </remarks>
internal static class RequestBatcherTraceTags
{
    /// <summary>
    /// Public request type's short name, without its namespace or an internal response-entry wrapper.
    /// </summary>
    internal const string RequestType = "requestbatcher.request.type";

    /// <summary>
    /// Registered batcher name; omitted for unnamed batchers.
    /// </summary>
    internal const string BatcherName = "requestbatcher.name";

    /// <summary>
    /// Execute: request count at admission, matching the submission-size metric (single: 1; collection: its length).
    /// </summary>
    internal const string SubmissionSize = "requestbatcher.submission.size";

    /// <summary>
    /// Handle: requests dispatched to this handler batch, excluding items canceled before dispatch.
    /// </summary>
    internal const string BatchSize = "requestbatcher.batch.size";

    /// <summary>
    /// Handle: distinct ExecuteAsync submissions contributing to this batch; multiple items from one call count once.
    /// </summary>
    internal const string SubmissionCount = "requestbatcher.batch.submission.count";

    /// <summary>
    /// Execute: true when the public call's task completes as canceled; omitted otherwise.
    /// </summary>
    internal const string Canceled = "requestbatcher.canceled";

    /// <summary>
    /// Failed Execute or Handle: exception type's short name, using the standard OpenTelemetry error.type key.
    /// </summary>
    internal const string ErrorType = "error.type";
}
