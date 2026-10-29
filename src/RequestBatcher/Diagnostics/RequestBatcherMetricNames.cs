namespace RequestBatcher.Diagnostics;

/// <summary>
/// Identifies instruments shared by recording and OpenTelemetry view configuration.
/// </summary>
internal static class RequestBatcherMetricNames
{
    internal const string SubmissionSize = "requestbatcher.submission.size";
    internal const string BatchSize = "requestbatcher.batch.size";
    internal const string HandleDuration = "requestbatcher.handle.duration";
    internal const string ExecuteDuration = "requestbatcher.execute.duration";
}
