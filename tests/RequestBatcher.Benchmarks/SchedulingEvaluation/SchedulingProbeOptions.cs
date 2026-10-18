namespace RequestBatcher.Benchmarks.SchedulingEvaluation;

internal sealed record SchedulingProbeOptions
{
    public string Workload { get; init; } = "fast";
    public string DatabaseOperation { get; init; } = "write";
    public double IntendedRate { get; init; } = 5_000;
    public int DurationMilliseconds { get; init; } = 1_000;
    public int MaxItems { get; init; } = 5_000;
    public int WarmupItems { get; init; } = 256;
    public int BatchSize { get; init; } = 1;
    public int MaxConcurrency { get; init; } = 8;
    public bool Offload { get; init; }
    public int CpuIterations { get; init; } = 100_000;
    public int BlockingMilliseconds { get; init; } = 1;

    public void Validate()
    {
        if (Workload is not ("fast" or "io" or "cpu" or "blocking" or "postgres" or "redis"))
        {
            throw new ArgumentOutOfRangeException(nameof(Workload));
        }

        if (DatabaseOperation is not ("write" or "read"))
        {
            throw new ArgumentOutOfRangeException(nameof(DatabaseOperation));
        }

        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(IntendedRate, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(MaxItems, 0);
        ArgumentOutOfRangeException.ThrowIfLessThan(DurationMilliseconds, 0);
        ArgumentOutOfRangeException.ThrowIfLessThan(WarmupItems, 0);
        ArgumentOutOfRangeException.ThrowIfLessThan(BatchSize, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxConcurrency, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(CpuIterations, 0);
        ArgumentOutOfRangeException.ThrowIfLessThan(BlockingMilliseconds, 0);
    }
}
