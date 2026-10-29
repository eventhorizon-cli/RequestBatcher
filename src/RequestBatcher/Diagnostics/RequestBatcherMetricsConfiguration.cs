namespace RequestBatcher.Diagnostics;

/// <summary>
/// Captures pipeline sizing for metrics setup without constructing the pipeline.
/// </summary>
internal sealed record RequestBatcherMetricsConfiguration(int BatchSize);
