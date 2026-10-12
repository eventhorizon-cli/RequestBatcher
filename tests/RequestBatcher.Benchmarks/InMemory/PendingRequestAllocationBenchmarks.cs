using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Engines;
using BenchmarkDotNet.Order;
using RequestBatcher.PendingRequests;

namespace RequestBatcher.Benchmarks.InMemory;

/// <summary>
/// Measures one completed pending request with and without a cancellable caller token.
/// </summary>
[MemoryDiagnoser]
[RankColumn]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
[SimpleJob(
    RunStrategy.Throughput,
    launchCount: 1,
    warmupCount: 3,
    iterationCount: 8,
    id: "PendingRequestAllocation")]
public class PendingRequestAllocationBenchmarks
{
    private CancellationTokenSource? _cancellation;

    [Params(false, true)]
    public bool Cancellable { get; set; }

    private CancellationToken RequestCancellationToken => _cancellation?.Token ?? default;

    [GlobalSetup]
    public void GlobalSetup()
    {
        if (Cancellable)
        {
            _cancellation = new CancellationTokenSource();
        }
    }

    [Benchmark(Description = "Complete one pending request")]
    public Task CompleteOnePendingRequest()
    {
        var pendingRequest = new PendingBatchRequest<int>(
            42,
            RequestCancellationToken,
            static _ => { });
        pendingRequest.TryStartProcessing();
        pendingRequest.CompleteSuccessfully();
        return pendingRequest.Completion;
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _cancellation?.Dispose();
        _cancellation = null;
    }
}
