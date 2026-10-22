using System.Diagnostics;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Engines;
using BenchmarkDotNet.Order;
using Microsoft.Extensions.DependencyInjection;

namespace RequestBatcher.Benchmarks.InMemory;

/// <summary>
/// Measures the public request submission path with tracing disabled, sampled out, and recorded.
/// </summary>
[MemoryDiagnoser]
[RankColumn]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
[SimpleJob(
    RunStrategy.Throughput,
    launchCount: 2,
    warmupCount: 5,
    iterationCount: 12,
    id: "TracingOverhead")]
[IterationTime(250)]
public class TracingOverheadBenchmarks
{
    private const int RequestCount = 256;
    private const int BatchSize = 32;
    private const int MaxConcurrency = 1;
    private readonly int[] _requests = Enumerable.Range(0, RequestCount).ToArray();
    private readonly int[][] _explicitGroups = CreateExplicitGroups();
    private ServiceProvider? _serviceProvider;
    private IRequestBatcher<int>? _requestBatcher;
    private CountingHandler? _handler;
    private ActivityListener? _listener;

    /// <summary>
    /// Selects the listener and sampling mode used by the benchmark.
    /// </summary>
    [Params(TracingMode.Disabled, TracingMode.Drop, TracingMode.Recorded)]
    public TracingMode ListenerMode { get; set; }

    private IRequestBatcher<int> RequestBatcher =>
        _requestBatcher ?? throw new InvalidOperationException("The benchmark has not been initialized.");

    private CountingHandler Handler =>
        _handler ?? throw new InvalidOperationException("The benchmark has not been initialized.");

    /// <summary>
    /// Configures the listener and excludes provider construction from measurement.
    /// </summary>
    [GlobalSetup]
    public void GlobalSetup()
    {
        if (ListenerMode != TracingMode.Disabled)
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = static source => source.Name == "RequestBatcher",
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ListenerMode switch
                {
                    TracingMode.Drop => ActivitySamplingResult.PropagationData,
                    TracingMode.Recorded => ActivitySamplingResult.AllDataAndRecorded,
                    _ => ActivitySamplingResult.None,
                },
            };
            ActivitySource.AddActivityListener(_listener);
        }

        var services = new ServiceCollection();
        services.AddRequestBatcher<int, CountingHandler>(
            ServiceLifetime.Singleton,
            options =>
            {
                options.BatchSize = BatchSize;
                options.MaxConcurrency = MaxConcurrency;
                options.MaxPendingRequests = RequestCount;
            });

        _serviceProvider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
        _handler = _serviceProvider.GetRequiredService<IRequestBatchHandler<int>>() as CountingHandler ??
            throw new InvalidOperationException("The tracing benchmark handler was not resolved.");
        _requestBatcher = _serviceProvider.GetRequiredService<IRequestBatcher<int>>();
    }

    /// <summary>
    /// Submits 256 requests individually and waits for all caller tasks.
    /// </summary>
    [Benchmark(Description = "256 single submissions")]
    public Task SingleSubmissionsAsync() => ExecuteWaveAsync(
        static (batcher, requests) =>
        {
            var tasks = new Task[requests.Length];
            for (var requestIndex = 0; requestIndex < requests.Length; requestIndex++)
            {
                tasks[requestIndex] = batcher.ExecuteAsync(requests[requestIndex]);
            }

            return Task.WhenAll(tasks);
        },
        _requests);

    /// <summary>
    /// Submits the same 256 requests as eight explicit groups of 32 requests.
    /// </summary>
    [Benchmark(Description = "8 explicit groups of 32")]
    public Task ExplicitGroupsAsync() => ExecuteWaveAsync(
        static (batcher, groups) =>
        {
            var tasks = new Task[groups.Length];
            for (var groupIndex = 0; groupIndex < groups.Length; groupIndex++)
            {
                tasks[groupIndex] = batcher.ExecuteAsync(groups[groupIndex]);
            }

            return Task.WhenAll(tasks);
        },
        _explicitGroups);

    /// <summary>
    /// Stops the coordinator and provider before removing the listener.
    /// </summary>
    [GlobalCleanup]
    public async Task GlobalCleanupAsync()
    {
        if (_serviceProvider is not null)
        {
            await _serviceProvider.DisposeAsync().ConfigureAwait(false);
            _serviceProvider = null;
        }

        _listener?.Dispose();
        _listener = null;
    }

    private async Task ExecuteWaveAsync<TSubmission>(
        Func<IRequestBatcher<int>, TSubmission, Task> submit,
        TSubmission submission)
    {
        Handler.Reset();
        await submit(RequestBatcher, submission).ConfigureAwait(false);
        Handler.ValidateProcessed(RequestCount);
    }

    private static int[][] CreateExplicitGroups()
    {
        var groups = new int[RequestCount / BatchSize][];
        for (var groupIndex = 0; groupIndex < groups.Length; groupIndex++)
        {
            groups[groupIndex] = Enumerable.Range(groupIndex * BatchSize, BatchSize).ToArray();
        }

        return groups;
    }

    /// <summary>
    /// Controls whether no listener, a non-recording listener, or a recording listener is installed.
    /// </summary>
    public enum TracingMode
    {
        /// <summary>No ActivityListener is installed.</summary>
        Disabled,

        /// <summary>The source is listened to but spans are sampled out.</summary>
        Drop,

        /// <summary>Call and batch Activities are created and recorded in memory; no processor or exporter is installed.</summary>
        Recorded,
    }

    private sealed class CountingHandler : IRequestBatchHandler<int>
    {
        private int _processed;

        public void Reset() => Volatile.Write(ref _processed, 0);

        public ValueTask HandleAsync(
            IReadOnlyList<int> requests,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Add(ref _processed, requests.Count);
            return ValueTask.CompletedTask;
        }

        public void ValidateProcessed(int expected)
        {
            var actual = Volatile.Read(ref _processed);
            if (actual != expected)
            {
                throw new InvalidOperationException(
                    $"The handler processed {actual} requests; expected {expected}.");
            }
        }
    }
}
