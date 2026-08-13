namespace RequestBatcher.Benchmarks.Infrastructure;

internal readonly record struct BatchExecutionSnapshot(
    int BatchCount,
    int RequestCount,
    int MaximumBatchSize);
