using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Engines;
using BenchmarkDotNet.Order;
using Microsoft.Extensions.DependencyInjection;

namespace RequestBatcher.Benchmarks.InMemory;

/// <summary>
/// Compares concurrent single-request submissions with one explicit submission under partition-key routing.
/// </summary>
[MemoryDiagnoser]
[RankColumn]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
[SimpleJob(
    RunStrategy.Throughput,
    launchCount: 2,
    warmupCount: 5,
    iterationCount: 12,
    id: "PartitionKeySubmission")]
[IterationTime(300)]
public class PartitionKeySubmissionBenchmarks
{
    private const int BatchSize = 100;
    private const int MaxConcurrency = 8;
    private const int MaxPendingRequests = 2_048;
    private const int PartitionCount = 4;
    private const int MaximumRequestCount = 1_000;

    private ServiceProvider? _serviceProvider;
    private PartitionDelayHandler? _handler;
    private IRequestBatcher<int>? _requestBatcher;
    private int[] _requests = [];

    private IRequestBatcher<int> RequestBatcher =>
        _requestBatcher ?? throw new InvalidOperationException("The benchmark has not been initialized.");

    private PartitionDelayHandler Handler =>
        _handler ?? throw new InvalidOperationException("The benchmark has not been initialized.");

    [Params(10, 100, 1_000)]
    public int RequestCount { get; set; }

    [Params(1, 10)]
    public int DelayMilliseconds { get; set; }

    [GlobalSetup]
    public void GlobalSetup()
    {
        _requests = Enumerable.Range(0, RequestCount).ToArray();

        var services = new ServiceCollection();
        services.AddRequestBatcher<int, PartitionDelayHandler>(
            ServiceLifetime.Singleton,
            options =>
            {
                options.BatchSize = BatchSize;
                options.MaxConcurrency = MaxConcurrency;
                options.MaxPendingRequests = MaxPendingRequests;
                options.UsePartitionKey(static request => request % 64, PartitionCount);
            });

        _serviceProvider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
        _handler = _serviceProvider.GetRequiredService<IRequestBatchHandler<int>>() as PartitionDelayHandler ??
            throw new InvalidOperationException("The partition-key handler was not resolved.");
        _handler.ConfigureDelay(DelayMilliseconds);
        _requestBatcher = _serviceProvider.GetRequiredService<IRequestBatcher<int>>();
    }

    [Benchmark(Baseline = true, Description = "Concurrent single-request submissions")]
    public async Task SingleRequestSubmissionsAsync()
    {
        Handler.BeginWave(RequestCount);

        var tasks = new Task[RequestCount];
        for (var index = 0; index < RequestCount; index++)
        {
            tasks[index] = RequestBatcher.ProcessAsync(_requests[index]);
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
        Handler.ValidateProcessed(RequestCount);
    }

    [Benchmark(Description = "One explicit submission")]
    public async Task ExplicitSubmissionAsync()
    {
        Handler.BeginWave(RequestCount);
        await RequestBatcher.ProcessAsync(_requests).ConfigureAwait(false);
        Handler.ValidateProcessed(RequestCount);
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

    private sealed class PartitionDelayHandler : IRequestBatchHandler<int>
    {
        private readonly int[] _processed = new int[MaximumRequestCount];
        private int _delayMilliseconds;

        public void ConfigureDelay(int delayMilliseconds) =>
            Volatile.Write(ref _delayMilliseconds, delayMilliseconds);

        public void BeginWave(int requestCount) =>
            Array.Clear(_processed, 0, requestCount);

        public async ValueTask HandleAsync(
            IReadOnlyList<int> requests,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Volatile.Read(ref _delayMilliseconds), cancellationToken)
                .ConfigureAwait(false);

            foreach (var request in requests)
            {
                Interlocked.Increment(ref _processed[request]);
            }
        }

        public void ValidateProcessed(int requestCount)
        {
            var total = 0;
            for (var request = 0; request < requestCount; request++)
            {
                var count = Volatile.Read(ref _processed[request]);
                if (count != 1)
                {
                    throw new InvalidOperationException(
                        $"Request {request} was processed {count} times; expected exactly once.");
                }

                total += count;
            }

            if (total != requestCount)
            {
                throw new InvalidOperationException(
                    $"The handler processed {total} requests; expected {requestCount}.");
            }
        }
    }
}
