using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Engines;
using BenchmarkDotNet.Order;
using Microsoft.Extensions.DependencyInjection;
using RequestBatcher.Benchmarks.Infrastructure;

namespace RequestBatcher.Benchmarks.InMemory;

/// <summary>
/// Measures explicit request submissions through the public dependency-injection batcher.
/// </summary>
[MemoryDiagnoser]
[RankColumn]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
[SimpleJob(
    RunStrategy.Throughput,
    launchCount: 2,
    warmupCount: 5,
    iterationCount: 12,
    id: "ExplicitSubmission")]
[IterationTime(300)]
public class ExplicitSubmissionBenchmarks
{
    private const int RequestCount = 512;
    private const int BatchSize = 32;
    private const int MaxConcurrency = 8;
    private const int MaxPendingRequests = 1_024;
    private const int SingleRequestCount = RequestCount / 2;

    private readonly int[] _requests = Enumerable.Range(0, RequestCount).ToArray();
    private ServiceProvider? _serviceProvider;
    private GatedHandler? _handler;
    private IRequestBatcher<int>? _requestBatcher;
    private int[][] _explicitGroups = [];
    private int[][] _mixedGroups = [];

    private IRequestBatcher<int> RequestBatcher =>
        _requestBatcher ?? throw new InvalidOperationException("The benchmark has not been initialized.");

    private GatedHandler Handler =>
        _handler ?? throw new InvalidOperationException("The benchmark has not been initialized.");

    [Params(16, 256)]
    public int GroupSize { get; set; }

    [GlobalSetup]
    public void GlobalSetup()
    {
        var services = new ServiceCollection();
        services.AddRequestBatcher<int, GatedHandler>(
            ServiceLifetime.Singleton,
            options =>
            {
                options.BatchSize = BatchSize;
                options.MaxConcurrency = MaxConcurrency;
                options.MaxPendingRequests = MaxPendingRequests;
                options.UsePartitionKey(static request => request, partitionCount: 4);
            });

        _serviceProvider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
        _handler = _serviceProvider.GetRequiredService<IRequestBatchHandler<int>>() as GatedHandler ??
            throw new InvalidOperationException("The explicit submission handler was not resolved.");
        _requestBatcher = _serviceProvider.GetRequiredService<IRequestBatcher<int>>();

        _explicitGroups = new int[RequestCount / GroupSize][];
        for (var groupIndex = 0; groupIndex < _explicitGroups.Length; groupIndex++)
        {
            _explicitGroups[groupIndex] = CreateGroup(groupIndex * GroupSize);
        }

        _mixedGroups = new int[SingleRequestCount / GroupSize][];
        for (var groupIndex = 0; groupIndex < _mixedGroups.Length; groupIndex++)
        {
            _mixedGroups[groupIndex] = CreateGroup(SingleRequestCount + groupIndex * GroupSize);
        }
    }

    private int[] CreateGroup(int offset)
    {
        var group = new int[GroupSize];
        Array.Copy(_requests, offset, group, 0, GroupSize);
        return group;
    }

    [Benchmark(Description = "All requests in explicit groups")]
    public async Task AllExplicitGroupsAsync()
    {
        Handler.BeginWave();
        var submissions = new Task[_explicitGroups.Length];
        for (var groupIndex = 0; groupIndex < _explicitGroups.Length; groupIndex++)
        {
            submissions[groupIndex] = RequestBatcher.ExecuteAsync(_explicitGroups[groupIndex]);
        }

        var all = Task.WhenAll(submissions);
        Handler.ReleaseWave();
        await all.ConfigureAwait(false);
        Handler.ValidateProcessed();
    }

    [Benchmark(Description = "Half single and half explicit groups")]
    public async Task MixedSingleAndExplicitGroupsAsync()
    {
        Handler.BeginWave();
        var submissions = new Task[SingleRequestCount + _mixedGroups.Length];
        for (var requestIndex = 0; requestIndex < SingleRequestCount; requestIndex++)
        {
            submissions[requestIndex] = RequestBatcher.ExecuteAsync(_requests[requestIndex]);
        }

        for (var groupIndex = 0; groupIndex < _mixedGroups.Length; groupIndex++)
        {
            submissions[SingleRequestCount + groupIndex] =
                RequestBatcher.ExecuteAsync(_mixedGroups[groupIndex]);
        }

        var all = Task.WhenAll(submissions);
        Handler.ReleaseWave();
        await all.ConfigureAwait(false);
        Handler.ValidateProcessed();
    }

    [GlobalCleanup]
    public async Task GlobalCleanupAsync()
    {
        if (_serviceProvider is not null)
        {
            await _serviceProvider.DisposeAsync().ConfigureAwait(false);
            _serviceProvider = null;
        }
    }

    private sealed class GatedHandler : IRequestBatchHandler<int>
    {
        private readonly BatchStartGate _startGate = new();
        private readonly int[] _processed = new int[RequestCount];

        public void BeginWave()
        {
            Array.Clear(_processed);
            _startGate.Reset();
        }

        public void ReleaseWave() => _startGate.Release();

        public async ValueTask HandleAsync(
            IReadOnlyList<int> requests,
            CancellationToken cancellationToken = default)
        {
            await _startGate.WaitAsync().ConfigureAwait(false);
            foreach (var request in requests)
            {
                Interlocked.Increment(ref _processed[request]);
            }
        }

        public void ValidateProcessed()
        {
            var total = 0;
            for (var request = 0; request < _processed.Length; request++)
            {
                var count = Volatile.Read(ref _processed[request]);
                if (count != 1)
                {
                    throw new InvalidOperationException(
                        $"Request {request} was processed {count} times; expected exactly once.");
                }

                total += count;
            }

            if (total != RequestCount)
            {
                throw new InvalidOperationException(
                    $"The handler processed {total} requests; expected {RequestCount}.");
            }
        }
    }
}
