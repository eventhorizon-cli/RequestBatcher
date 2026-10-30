namespace RequestBatcher.Diagnostics.Metrics;

/// <summary>
/// Captures batcher sizing for metrics setup without constructing the batcher.
/// </summary>
internal sealed record RequestBatcherMetricsConfiguration(int BatchSize);
