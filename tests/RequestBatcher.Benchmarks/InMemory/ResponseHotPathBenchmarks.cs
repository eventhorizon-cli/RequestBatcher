using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Engines;
using BenchmarkDotNet.Order;
using Microsoft.Extensions.DependencyInjection;

namespace RequestBatcher.Benchmarks.InMemory;

/// <summary>
/// Compares typed and delegate response handling for concurrent single calls and one explicit batch.
/// </summary>
/// <remarks>
/// Each invocation submits 256 distinct integer requests through the public API, checks response order and
/// exactly-once processing, and verifies handler batch-size and concurrency bounds. The typed handler and static
/// delegate share the same service-free response implementation. Provider creation is outside measurement, and the
/// configured BenchmarkDotNet warmup binds the delegate before measured iterations. Run with
/// <c>--unrollFactor 1</c> to keep simulated 20 ms I/O waves bounded. Zero-delay iterations can be shorter than
/// BenchmarkDotNet's recommended duration, so compare only matched source snapshots and treat those time results as
/// short-iteration estimates.
/// </remarks>
[MemoryDiagnoser]
[RankColumn]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
[SimpleJob(
    RunStrategy.Throughput,
    launchCount: 2,
    warmupCount: 3,
    iterationCount: 8,
    invocationCount: 4,
    id: "ResponseHotPaths")]
public class ResponseHotPathBenchmarks
{
    private const int RequestCount = 256;
    private const int BatchSize = 32;
    private const int MaxConcurrency = 8;

    private static readonly WaveProbe _probe = new();
    private static readonly Func<
        IReadOnlyList<RequestBatchEntry<int, int>>,
        CancellationToken,
        ValueTask> _delegateHandler = HandleDelegateAsync;
    private static readonly int[] _requests = Enumerable.Range(0, RequestCount).ToArray();

    private ServiceProvider? _serviceProvider;
    private IRequestBatcher<int, int>? _batcher;
    private long _singleWaves;
    private long _explicitWaves;

    /// <summary>
    /// Gets or sets whether the wave uses the static delegate handler.
    /// </summary>
    [Params(false, true)]
    public bool UseDelegate { get; set; }

    /// <summary>
    /// Gets or sets the simulated handler delay in milliseconds.
    /// </summary>
    [Params(0, 20)]
    public int HandlerDelayMilliseconds { get; set; }

    /// <summary>
    /// Builds the public batcher and warms both submission paths outside measurement.
    /// </summary>
    [GlobalSetup]
    public void GlobalSetup()
    {
        _probe.Configure(HandlerDelayMilliseconds);

        var services = new ServiceCollection();
        if (UseDelegate)
        {
            services.AddDelegateRequestBatcher<int, int>(ConfigureOptions);
        }
        else
        {
            services.AddRequestBatcher<int, int, TypedResponseHandler>(
                ServiceLifetime.Singleton,
                ConfigureOptions);
        }

        _serviceProvider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
        _batcher = _serviceProvider.GetRequiredService<IRequestBatcher<int, int>>();

        RunConcurrentSingleWaveAsync().GetAwaiter().GetResult();
        RunExplicitBatchWaveAsync().GetAwaiter().GetResult();
        _singleWaves = 0;
        _explicitWaves = 0;
    }

    /// <summary>
    /// Measures one wave of 256 concurrent single response submissions.
    /// </summary>
    [Benchmark(Description = "Concurrent single calls")]
    public Task ConcurrentSingleCallsAsync() => RunConcurrentSingleWaveAsync();

    /// <summary>
    /// Measures one explicit response submission containing all 256 requests.
    /// </summary>
    [Benchmark(Description = "One explicit batch")]
    public Task ExplicitBatchAsync() => RunExplicitBatchWaveAsync();

    /// <summary>
    /// Drains and disposes the provider before printing successful wave validation counts.
    /// </summary>
    [GlobalCleanup]
    public async Task GlobalCleanupAsync()
    {
        if (_serviceProvider is not null)
        {
            await _serviceProvider.DisposeAsync().ConfigureAwait(false);
            _serviceProvider = null;
        }

        if (_singleWaves > 0)
        {
            WriteValidation("ConcurrentSingleCalls", _singleWaves);
        }

        if (_explicitWaves > 0)
        {
            WriteValidation("ExplicitBatch", _explicitWaves);
        }
    }

    private IRequestBatcher<int, int> Batcher =>
        _batcher ?? throw new InvalidOperationException("The benchmark has not been initialized.");

    private static void ConfigureOptions(RequestBatchOptions<int> options)
    {
        options.BatchSize = BatchSize;
        options.MaxConcurrency = MaxConcurrency;
        options.MaxPendingRequests = RequestCount;
        options.FullMode = RequestBatchFullMode.Wait;
    }

    private static ValueTask HandleDelegateAsync(
        IReadOnlyList<RequestBatchEntry<int, int>> entries,
        CancellationToken cancellationToken) =>
        _probe.HandleAsync(entries, cancellationToken);

    private async Task RunConcurrentSingleWaveAsync()
    {
        _probe.BeginWave();
        var tasks = new Task<int>[RequestCount];
        for (var index = 0; index < RequestCount; index++)
        {
            tasks[index] = UseDelegate
                ? Batcher.ExecuteAsync(_requests[index], _delegateHandler)
                : Batcher.ExecuteAsync(_requests[index]);
        }

        var responses = await Task.WhenAll(tasks).ConfigureAwait(false);
        ValidateResponses(responses);
        _probe.ValidateWave();
        _singleWaves++;
    }

    private async Task RunExplicitBatchWaveAsync()
    {
        _probe.BeginWave();
        var submission = UseDelegate
            ? Batcher.ExecuteAsync(_requests, _delegateHandler)
            : Batcher.ExecuteAsync(_requests);
        var responses = await submission.ConfigureAwait(false);
        ValidateResponses(responses);
        _probe.ValidateWave();
        _explicitWaves++;
    }

    private static void ValidateResponses(IReadOnlyList<int> responses)
    {
        if (responses.Count != RequestCount)
        {
            throw new InvalidOperationException(
                $"Received {responses.Count} responses; expected {RequestCount}.");
        }

        for (var index = 0; index < RequestCount; index++)
        {
            var expected = _requests[index] * 17 + 3;
            if (responses[index] != expected)
            {
                throw new InvalidOperationException(
                    $"Response at index {index} was {responses[index]}; expected {expected}.");
            }
        }
    }

    private void WriteValidation(string method, long waves) => Console.WriteLine(
        $"Response hot-path validation: Method={method}, UseDelegate={UseDelegate}, " +
        $"HandlerDelayMilliseconds={HandlerDelayMilliseconds}, Waves={waves}, RequestsPerWave={RequestCount}, " +
        "ExactlyOnce=True, ResponseOrder=True, Bounds=True");

    private sealed class TypedResponseHandler : IRequestBatchHandler<int, int>
    {
        public ValueTask HandleAsync(
            IReadOnlyList<RequestBatchEntry<int, int>> entries,
            CancellationToken cancellationToken = default) =>
            _probe.HandleAsync(entries, cancellationToken);
    }

    private sealed class WaveProbe
    {
        private readonly int[] _processed = new int[RequestCount];
        private int _delayMilliseconds;
        private int _activeBatches;
        private int _maximumActiveBatches;
        private int _maximumBatchSize;
        private int _handlerCalls;

        public void Configure(int delayMilliseconds)
        {
            if (delayMilliseconds is not (0 or 20))
            {
                throw new ArgumentOutOfRangeException(nameof(delayMilliseconds));
            }

            Volatile.Write(ref _delayMilliseconds, delayMilliseconds);
        }

        public void BeginWave()
        {
            if (Volatile.Read(ref _activeBatches) != 0)
            {
                throw new InvalidOperationException("A previous wave still has active handler batches.");
            }

            Array.Clear(_processed);
            Volatile.Write(ref _maximumActiveBatches, 0);
            Volatile.Write(ref _maximumBatchSize, 0);
            Volatile.Write(ref _handlerCalls, 0);
        }

        public ValueTask HandleAsync(
            IReadOnlyList<RequestBatchEntry<int, int>> entries,
            CancellationToken cancellationToken)
        {
            var batchSize = entries.Count;
            if (batchSize is < 1 or > BatchSize)
            {
                throw new InvalidOperationException(
                    $"Observed handler batch size {batchSize}; expected 1..{BatchSize}.");
            }

            var activeBatches = Interlocked.Increment(ref _activeBatches);
            UpdateMaximum(ref _maximumActiveBatches, activeBatches);
            if (activeBatches > MaxConcurrency)
            {
                Interlocked.Decrement(ref _activeBatches);
                throw new InvalidOperationException(
                    $"Observed {activeBatches} active handler batches; limit is {MaxConcurrency}.");
            }

            UpdateMaximum(ref _maximumBatchSize, batchSize);
            Interlocked.Increment(ref _handlerCalls);

            var delayMilliseconds = Volatile.Read(ref _delayMilliseconds);
            if (delayMilliseconds == 0)
            {
                try
                {
                    ProcessEntries(entries);
                }
                finally
                {
                    Interlocked.Decrement(ref _activeBatches);
                }

                return ValueTask.CompletedTask;
            }

            return HandleAfterDelayAsync(entries, cancellationToken, delayMilliseconds);
        }

        public void ValidateWave()
        {
            var maximumActiveBatches = Volatile.Read(ref _maximumActiveBatches);
            var maximumBatchSize = Volatile.Read(ref _maximumBatchSize);
            if (Volatile.Read(ref _activeBatches) != 0 ||
                Volatile.Read(ref _handlerCalls) == 0 ||
                maximumActiveBatches is < 1 or > MaxConcurrency ||
                maximumBatchSize is < 1 or > BatchSize)
            {
                throw new InvalidOperationException(
                    $"Invalid bounds: max batch={maximumBatchSize}/{BatchSize}, " +
                    $"peak concurrency={maximumActiveBatches}/{MaxConcurrency}.");
            }

            for (var request = 0; request < RequestCount; request++)
            {
                var count = Volatile.Read(ref _processed[request]);
                if (count != 1)
                {
                    throw new InvalidOperationException(
                        $"Request {request} was processed {count} times; expected exactly once.");
                }
            }
        }

        private async ValueTask HandleAfterDelayAsync(
            IReadOnlyList<RequestBatchEntry<int, int>> entries,
            CancellationToken cancellationToken,
            int delayMilliseconds)
        {
            try
            {
                await Task.Delay(delayMilliseconds, cancellationToken).ConfigureAwait(false);
                ProcessEntries(entries);
            }
            finally
            {
                Interlocked.Decrement(ref _activeBatches);
            }
        }

        private void ProcessEntries(IReadOnlyList<RequestBatchEntry<int, int>> entries)
        {
            for (var index = 0; index < entries.Count; index++)
            {
                var request = entries[index].Request;
                if ((uint)request >= RequestCount)
                {
                    throw new InvalidOperationException($"Observed unexpected request ID {request}.");
                }

                Interlocked.Increment(ref _processed[request]);
            }

            entries.SetResponses(static request => request * 17 + 3);
        }

        private static void UpdateMaximum(ref int maximum, int value)
        {
            var current = Volatile.Read(ref maximum);
            while (value > current)
            {
                var previous = Interlocked.CompareExchange(ref maximum, value, current);
                if (previous == current)
                {
                    return;
                }

                current = previous;
            }
        }
    }
}
