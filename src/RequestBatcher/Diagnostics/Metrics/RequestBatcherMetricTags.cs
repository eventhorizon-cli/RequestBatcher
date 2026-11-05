namespace RequestBatcher.Diagnostics.Metrics;

/// <summary>
/// Defines metric attribute keys and their fixed submission-kind and outcome values.
/// </summary>
/// <remarks>
/// Keep OpenTelemetry's dotted attribute names here; Prometheus translation may replace dots with underscores in labels.
/// </remarks>
internal static class RequestBatcherMetricTags
{
    /// <summary>
    /// Public request type's short name on every metric, without its namespace or an internal response-entry wrapper.
    /// </summary>
    internal const string RequestType = "requestbatcher.request.type";

    /// <summary>
    /// Registered batcher name on every metric; omitted for unnamed batchers.
    /// </summary>
    internal const string BatcherName = "requestbatcher.name";

    /// <summary>
    /// Submission-size and Execute-duration dimension identifying the single-request or collection overload.
    /// </summary>
    internal const string SubmissionKind = "requestbatcher.submission.kind";

    /// <summary>
    /// Execute-duration dimension identifying the public call's final success, error, or cancellation outcome.
    /// </summary>
    internal const string Outcome = "requestbatcher.outcome";

    /// <summary>
    /// SubmissionKind value for a single-request overload.
    /// </summary>
    internal const string SingleSubmission = "single";

    /// <summary>
    /// SubmissionKind value for a collection overload, including empty and one-item collections.
    /// </summary>
    internal const string MultiSubmission = "multi";

    /// <summary>
    /// Outcome value for a public call that completed successfully.
    /// </summary>
    internal const string Success = "success";

    /// <summary>
    /// Outcome value for a failed public call, including a faulted OperationCanceledException.
    /// </summary>
    internal const string Error = "error";

    /// <summary>
    /// Outcome value for a public call whose task completed as canceled.
    /// </summary>
    internal const string Canceled = "canceled";
}
