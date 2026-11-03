using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Engines;
using BenchmarkDotNet.Order;
using Microsoft.Extensions.DependencyInjection;

namespace RequestBatcher.Benchmarks.InMemory;

/// <summary>
/// Compares synchronous dispatch overhead for concurrent single submissions and one explicit submission.
/// </summary>
/// <remarks>
/// Each operation submits the same 1,024 distinct integer requests through the public batcher API. The
/// unkeyed in-memory queue uses its default single partition, and the singleton typed handler performs only indexed
/// request accounting. No telemetry listeners or exporters are registered. Each benchmark uses
/// <see cref="BenchmarkAttribute.OperationsPerInvoke"/> to report time and allocations per request.
/// </remarks>
[MemoryDiagnoser]
[RankColumn]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
[SimpleJob(
    RunStrategy.Throughput,
    launchCount: 2,
    warmupCount: 3,
    iterationCount: 10,
    id: "DispatchLoopOverhead")]
public class DispatchLoopOverheadBenchmarks
{
    private const int RequestCount = 1_024;
    private const int MaxConcurrency = 8;

    private readonly int[] _requests = Enumerable.Range(0, RequestCount).ToArray();
    private ServiceProvider _serviceProvider = null!;
    private IRequestBatcher<int> _requestBatcher = null!;
    private SynchronousHandler _handler = null!;
    private long _validatedWaves;

    /// <summary>Gets or sets the maximum request count passed to one handler invocation.</summary>
    [Params(1, 32)]
    public int BatchSize { get; set; }

    /// <summary>Builds the public batcher and warms both submission paths outside measurement.</summary>
    [GlobalSetup]
    public void GlobalSetup()
    {
        var services = new ServiceCollection();
        services.AddRequestBatcher<int, SynchronousHandler>(
            ServiceLifetime.Singleton,
            options =>
            {
                options.BatchSize = BatchSize;
                options.MaxConcurrency = MaxConcurrency;
                options.MaxPendingRequests = RequestCount;
                options.FullMode = RequestBatchFullMode.Wait;
            });

        _serviceProvider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
        _requestBatcher = _serviceProvider.GetRequiredService<IRequestBatcher<int>>();
        _handler = _serviceProvider.GetRequiredService<IRequestBatchHandler<int>>() as SynchronousHandler ??
            throw new InvalidOperationException("The synchronous dispatch benchmark handler was not resolved.");

        RunWaveAsync(explicitSubmission: false).GetAwaiter().GetResult();
        RunWaveAsync(explicitSubmission: true).GetAwaiter().GetResult();
    }

    /// <summary>Measures 1,024 concurrent single submissions, started before awaiting their tasks.</summary>
    [Benchmark(
        Baseline = true,
        OperationsPerInvoke = RequestCount,
        Description = "Concurrent single submissions")]
    public Task ConcurrentSingleSubmissionsAsync() => RunWaveAsync(explicitSubmission: false);

    /// <summary>Measures one explicit submission containing all 1,024 requests.</summary>
    [Benchmark(
        OperationsPerInvoke = RequestCount,
        Description = "One explicit submission")]
    public Task ExplicitSubmissionAsync() => RunWaveAsync(explicitSubmission: true);

    /// <summary>Drains the batcher and disposes its service provider outside measurement.</summary>
    [GlobalCleanup]
    public async Task GlobalCleanupAsync()
    {
        await _serviceProvider.DisposeAsync().ConfigureAwait(false);
        Console.WriteLine(
            $"Dispatch overhead validation: BatchSize={BatchSize}, Waves={_validatedWaves}, " +
            $"RequestsPerWave={RequestCount}, ExactlyOnce=True, DispatchBounds=True");
    }

    private async Task RunWaveAsync(bool explicitSubmission)
    {
        _handler.BeginWave(BatchSize);
        if (explicitSubmission)
        {
            await _requestBatcher.ExecuteAsync(_requests).ConfigureAwait(false);
        }
        else
        {
            var submissions = new Task[RequestCount];
            for (var index = 0; index < RequestCount; index++)
            {
                submissions[index] = _requestBatcher.ExecuteAsync(_requests[index]);
            }

            await Task.WhenAll(submissions).ConfigureAwait(false);
        }

        _handler.ValidateWave();
        _validatedWaves++;
    }

    private sealed class SynchronousHandler : IRequestBatchHandler<int>
    {
        private readonly int[] _processed = new int[RequestCount];
        private int _expectedBatchSize;
        private int _activeBatches;
        private int _maximumActiveBatches;
        private int _maximumBatchSize;

        public void BeginWave(int batchSize)
        {
            if (Volatile.Read(ref _activeBatches) != 0)
            {
                throw new InvalidOperationException("A previous benchmark wave is still running.");
            }

            Array.Clear(_processed);
            Volatile.Write(ref _expectedBatchSize, batchSize);
            Volatile.Write(ref _maximumActiveBatches, 0);
            Volatile.Write(ref _maximumBatchSize, 0);
        }

        public ValueTask HandleAsync(
            IReadOnlyList<int> requests,
            CancellationToken cancellationToken = default)
        {
            var requestCount = requests.Count;
            var expectedBatchSize = Volatile.Read(ref _expectedBatchSize);
            if (requestCount is < 1 || requestCount > expectedBatchSize)
            {
                throw new InvalidOperationException(
                    $"Observed handler batch size {requestCount}; expected 1..{expectedBatchSize}.");
            }

            var activeBatches = Interlocked.Increment(ref _activeBatches);
            UpdateMaximum(ref _maximumActiveBatches, activeBatches);
            try
            {
                if (activeBatches > MaxConcurrency)
                {
                    throw new InvalidOperationException(
                        $"Observed {activeBatches} concurrent handler batches; limit is {MaxConcurrency}.");
                }

                UpdateMaximum(ref _maximumBatchSize, requestCount);
                for (var index = 0; index < requestCount; index++)
                {
                    var request = requests[index];
                    if ((uint)request >= RequestCount)
                    {
                        throw new InvalidOperationException($"Observed unexpected request ID {request}.");
                    }

                    Interlocked.Increment(ref _processed[request]);
                }
            }
            finally
            {
                Interlocked.Decrement(ref _activeBatches);
            }

            return ValueTask.CompletedTask;
        }

        public void ValidateWave()
        {
            var expectedBatchSize = Volatile.Read(ref _expectedBatchSize);
            var maximumBatchSize = Volatile.Read(ref _maximumBatchSize);
            var maximumActiveBatches = Volatile.Read(ref _maximumActiveBatches);
            if (Volatile.Read(ref _activeBatches) != 0 ||
                maximumBatchSize is < 1 || maximumBatchSize > expectedBatchSize ||
                maximumActiveBatches is < 1 || maximumActiveBatches > MaxConcurrency)
            {
                throw new InvalidOperationException(
                    $"Observed invalid dispatch bounds: max batch={maximumBatchSize}/{expectedBatchSize}, " +
                    $"max concurrency={maximumActiveBatches}/{MaxConcurrency}.");
            }

            for (var request = 0; request < _processed.Length; request++)
            {
                var count = Volatile.Read(ref _processed[request]);
                if (count != 1)
                {
                    throw new InvalidOperationException(
                        $"Request {request} was processed {count} times; expected exactly once.");
                }
            }
        }

        private static void UpdateMaximum(ref int maximum, int value)
        {
            var current = Volatile.Read(ref maximum);
            while (value > current)
            {
                var observed = Interlocked.CompareExchange(ref maximum, value, current);
                if (observed == current)
                {
                    return;
                }

                current = observed;
            }
        }
    }
}
