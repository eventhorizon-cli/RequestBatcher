namespace RequestBatcher.Diagnostics;

/// <summary>
/// Defines the names of RequestBatcher metric instruments.
/// </summary>
internal static class RequestBatcherMetricNames
{
    internal const string SubmissionSize = "requestbatcher.submission.size";
    internal const string BatchSize = "requestbatcher.batch.size";
    internal const string HandleDuration = "requestbatcher.handle.duration";
    internal const string ExecuteDuration = "requestbatcher.execute.duration";
    internal const string Backlog = "requestbatcher.backlog";
}
