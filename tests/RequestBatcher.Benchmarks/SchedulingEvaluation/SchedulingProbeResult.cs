namespace RequestBatcher.Benchmarks.SchedulingEvaluation;

internal sealed record SchedulingProbeResult
{
    public string Workload { get; init; } = "";
    public string DatabaseOperation { get; init; } = "write";
    public bool Offload { get; init; }
    public int BatchSize { get; init; }
    public int MaxConcurrency { get; init; }
    public double IntendedRate { get; init; }
    public int IssuedItems { get; init; }
    public double IssueMilliseconds { get; init; }
    public double ActualIssueRate { get; init; }
    public double ElapsedMilliseconds { get; init; }
    public double Throughput { get; init; }
    public double CallerP50Milliseconds { get; init; }
    public double CallerP95Milliseconds { get; init; }
    public double CallerP99Milliseconds { get; init; }
    public double ActiveSlotWallUtilization { get; init; }
    public int PeakActiveSlots { get; init; }
    public int BatchCount { get; init; }
    public double MeanBatchSize { get; init; }
    public int MaximumBatchSize { get; init; }
    public long AllocatedBytes { get; init; }
    public int Gen0Collections { get; init; }
    public int Gen1Collections { get; init; }
    public int Gen2Collections { get; init; }
    public long ThreadPoolPendingDelta { get; init; }
    public long ThreadPoolCompletedDelta { get; init; }
    public bool ExactOnce { get; init; }
}
