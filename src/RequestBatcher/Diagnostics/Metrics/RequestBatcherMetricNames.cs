namespace RequestBatcher.Diagnostics.Metrics;

/// <summary>
/// Defines the names of RequestBatcher metric instruments.
/// </summary>
/// <remarks>
/// Use dotted OpenTelemetry names; exporters may translate names and append unit or instrument-type suffixes.
/// </remarks>
internal static class RequestBatcherMetricNames
{
    /// <summary>
    /// Names the histogram of request items in submissions reaching coordinator admission.
    /// </summary>
    /// <remarks>
    /// Values use the <c>{request}</c> unit: 1 for a single request or the materialized collection count, including 0.
    /// </remarks>
    internal const string SubmissionSize = "requestbatcher.submission.size";

    /// <summary>
    /// Names the histogram of request items in completed Handler batches.
    /// </summary>
    /// <remarks>
    /// Values use the <c>{request}</c> unit. Includes failed Handler attempts; excludes items canceled before dispatch.
    /// </remarks>
    internal const string BatchSize = "requestbatcher.batch.size";

    /// <summary>
    /// Names the histogram of Handler execution and scope disposal duration per batch, in milliseconds.
    /// </summary>
    internal const string HandleDuration = "requestbatcher.handle.duration";

    /// <summary>
    /// Names the histogram of end-to-end public ExecuteAsync call duration, in milliseconds.
    /// </summary>
    /// <remarks>
    /// Duration includes capacity waiting, queueing, Handler batches, and response assembly.
    /// </remarks>
    internal const string ExecuteDuration = "requestbatcher.execute.duration";

    /// <summary>
    /// Names the observable gauge of approximate enqueued request items not yet pulled, in <c>{request}</c> units.
    /// </summary>
    /// <remarks>
    /// Excludes producer capacity waits and active Handler work; canceled records remain included until pulled.
    /// </remarks>
    internal const string Backlog = "requestbatcher.backlog";
}
