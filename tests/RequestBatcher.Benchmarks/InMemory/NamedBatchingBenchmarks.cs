using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Engines;
using Microsoft.Extensions.DependencyInjection;
using RequestBatcher.Benchmarks.Infrastructure;

namespace RequestBatcher.Benchmarks.InMemory;

/// <summary>
/// Compares named and unnamed response batchers for the same primitive request workload.
/// </summary>
[MemoryDiagnoser]
[CategoriesColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[SimpleJob(
    RunStrategy.Throughput,
    launchCount: 2,
    warmupCount: 5,
    iterationCount: 12,
    id: "NamedBatching")]
[IterationTime(300)]
public class NamedBatchingBenchmarks
{
    private const int RequestCount = 256;
    private const int BatchSize = 32;
    private const int MaxConcurrency = 8;
    private const int MaxPendingRequests = 512;
    private const string BatcherName = "named";

    private readonly int[] _requests = Enumerable.Range(0, RequestCount).ToArray();
    private ServiceProvider? _serviceProvider;
    private WaveState? _waveState;
    private IRequestBatcher<int, int>? _batcher;

    private WaveState State =>
        _waveState ?? throw new InvalidOperationException("The benchmark has not been initialized.");

    private IRequestBatcher<int, int> Batcher =>
        _batcher ?? throw new InvalidOperationException("The benchmark has not been initialized.");

    /// <summary>
    /// Selects the handler lifetime used by the named and unnamed cases.
    /// </summary>
    [Params(ServiceLifetime.Singleton, ServiceLifetime.Scoped)]
    public ServiceLifetime HandlerLifetime { get; set; }

    /// <summary>
    /// Builds the unnamed batcher for the single and explicit submission cases.
    /// </summary>
    [GlobalSetup(Targets = new[] { nameof(UnnamedSingleAsync), nameof(UnnamedExplicitAsync) })]
    public void SetupUnnamed() => Setup(named: false);

    /// <summary>
    /// Builds the named batcher for the single and explicit submission cases.
    /// </summary>
    [GlobalSetup(Targets = new[] { nameof(NamedSingleAsync), nameof(NamedExplicitAsync) })]
    public void SetupNamed() => Setup(named: true);

    /// <summary>
    /// Measures the unnamed single-request submission baseline.
    /// </summary>
    [BenchmarkCategory("Single")]
    [Benchmark(Baseline = true)]
    public Task UnnamedSingleAsync() => RunSingleAsync();

    /// <summary>
    /// Measures named single-request submission.
    /// </summary>
    [BenchmarkCategory("Single")]
    [Benchmark]
    public Task NamedSingleAsync() => RunSingleAsync();

    /// <summary>
    /// Measures the unnamed explicit-array submission baseline.
    /// </summary>
    [BenchmarkCategory("Explicit")]
    [Benchmark(Baseline = true)]
    public Task UnnamedExplicitAsync() => RunExplicitAsync();

    /// <summary>
    /// Measures named explicit-array submission.
    /// </summary>
    [BenchmarkCategory("Explicit")]
    [Benchmark]
    public Task NamedExplicitAsync() => RunExplicitAsync();

    /// <summary>
    /// Disposes the provider created for the current benchmark case.
    /// </summary>
    [GlobalCleanup]
    public async Task GlobalCleanupAsync()
    {
        if (_serviceProvider is not null)
        {
            await _serviceProvider.DisposeAsync().ConfigureAwait(false);
            _serviceProvider = null;
        }
    }

    private void Setup(bool named)
    {
        var services = new ServiceCollection();
        _waveState = new WaveState();
        services.AddSingleton(_waveState);

        if (named)
        {
            services.AddRequestBatcher<int, int, WaveHandler>(
                BatcherName,
                HandlerLifetime,
                ConfigureOptions);
        }
        else
        {
            services.AddRequestBatcher<int, int, WaveHandler>(
                HandlerLifetime,
                ConfigureOptions);
        }

        _serviceProvider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
        _batcher = named
            ? _serviceProvider.GetRequiredKeyedService<IRequestBatcher<int, int>>(BatcherName)
            : _serviceProvider.GetRequiredService<IRequestBatcher<int, int>>();
    }

    private static void ConfigureOptions(RequestBatchOptions<int> options)
    {
        options.BatchSize = BatchSize;
        options.MaxConcurrency = MaxConcurrency;
        options.MaxPendingRequests = MaxPendingRequests;
        options.FullMode = RequestBatchFullMode.Wait;
    }

    private Task RunSingleAsync() =>
        RunWaveAsync(static (batcher, requests) =>
        {
            var tasks = new Task<int>[requests.Length];
            for (var index = 0; index < requests.Length; index++)
            {
                tasks[index] = batcher.ExecuteAsync(requests[index]);
            }

            return AwaitAllAsync(tasks);
        });

    private Task RunExplicitAsync() =>
        RunWaveAsync(static (batcher, requests) => batcher.ExecuteAsync(requests));

    private static async Task<IReadOnlyList<int>> AwaitAllAsync(Task<int>[] tasks) =>
        await Task.WhenAll(tasks).ConfigureAwait(false);

    private async Task RunWaveAsync(
        Func<IRequestBatcher<int, int>, int[], Task<IReadOnlyList<int>>> submit)
    {
        State.BeginWave();
        try
        {
            var responsesTask = submit(Batcher, _requests);
            State.ReleaseWave();
            var responses = await responsesTask.ConfigureAwait(false);
            State.Validate(responses);
        }
        finally
        {
            State.ReleaseWave();
        }
    }

    private sealed class WaveHandler(WaveState state) : IRequestBatchHandler<int, int>
    {
        public async ValueTask HandleAsync(
            IReadOnlyList<RequestBatchEntry<int, int>> entries,
            CancellationToken cancellationToken = default)
        {
            await state.StartGate.WaitAsync().ConfigureAwait(false);
            foreach (var entry in entries)
            {
                Interlocked.Increment(ref state.Processed[entry.Request]);
                entry.SetResponse(entry.Request);
            }
        }
    }

    private sealed class WaveState
    {
        public BatchStartGate StartGate { get; } = new();

        public int[] Processed { get; } = new int[RequestCount];

        public void BeginWave()
        {
            Array.Clear(Processed);
            StartGate.Reset();
        }

        public void ReleaseWave() => StartGate.Release();

        public void Validate(IReadOnlyList<int> responses)
        {
            if (responses.Count != RequestCount)
            {
                throw new InvalidOperationException(
                    $"The batcher returned {responses.Count} responses; expected {RequestCount}.");
            }

            for (var request = 0; request < RequestCount; request++)
            {
                if (Volatile.Read(ref Processed[request]) != 1)
                {
                    throw new InvalidOperationException(
                        $"Request {request} was processed {Processed[request]} times; expected exactly once.");
                }

                if (responses[request] != request)
                {
                    throw new InvalidOperationException(
                        $"Response {responses[request]} did not match request {request}.");
                }
            }
        }
    }
}
