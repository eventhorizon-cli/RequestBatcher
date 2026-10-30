using System.Diagnostics;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Engines;
using BenchmarkDotNet.Order;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace RequestBatcher.Benchmarks.InMemory;

/// <summary>
/// Measures observability overhead for an asynchronous, named response batcher workload.
/// </summary>
[MemoryDiagnoser]
[RankColumn]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
[SimpleJob(
    RunStrategy.Throughput,
    launchCount: 2,
    warmupCount: 5,
    iterationCount: 12,
    id: "ObservabilityOverhead")]
[IterationTime(500)]
public class ObservabilityOverheadBenchmarks
{
    private const int RequestCount = 256;
    private const int BatchSize = 32;
    private const int MaxConcurrency = 4;
    private const int MaxPendingRequests = RequestCount;
    private const string BatcherName = "pricing-queries";
    private readonly int[] _requests = Enumerable.Range(0, RequestCount).ToArray();
    private ServiceProvider? _serviceProvider;
    private MeterProvider? _meterProvider;
    private TracerProvider? _tracerProvider;
    private CountingCollection<Metric>? _exportedMetrics;
    private CountingCollection<Activity>? _exportedActivities;
    private IRequestBatcher<int, int>? _batcher;

    /// <summary>
    /// Selects which in-process OpenTelemetry providers are active during the measurement.
    /// </summary>
    [Params(ObservabilityMode.Disabled, ObservabilityMode.Metrics, ObservabilityMode.TracingAndMetrics)]
    public ObservabilityMode Mode { get; set; }

    private IRequestBatcher<int, int> Batcher =>
        _batcher ?? throw new InvalidOperationException("The benchmark has not been initialized.");

    /// <summary>
    /// Creates the named batcher and provider listeners before timing starts.
    /// </summary>
    [GlobalSetup]
    public void GlobalSetup()
    {
        if (Mode is ObservabilityMode.Metrics or ObservabilityMode.TracingAndMetrics)
        {
            _exportedMetrics = new CountingCollection<Metric>();
            _meterProvider = Sdk.CreateMeterProviderBuilder()
                .AddRequestBatcherInstrumentation()
                .AddInMemoryExporter(_exportedMetrics)
                .Build();
        }

        if (Mode == ObservabilityMode.TracingAndMetrics)
        {
            _exportedActivities = new CountingCollection<Activity>();
            _tracerProvider = Sdk.CreateTracerProviderBuilder()
                .AddRequestBatcherInstrumentation()
                .AddInMemoryExporter(_exportedActivities)
                .Build();
        }

        var services = new ServiceCollection();
        services.AddRequestBatcher<int, int, PricingHandler>(
            BatcherName,
            ServiceLifetime.Singleton,
            options =>
            {
                options.BatchSize = BatchSize;
                options.MaxConcurrency = MaxConcurrency;
                options.MaxPendingRequests = MaxPendingRequests;
            });

        _serviceProvider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
        _batcher = _serviceProvider.GetRequiredKeyedService<IRequestBatcher<int, int>>(BatcherName);
    }

    /// <summary>
    /// Submits 256 requests individually and waits for every response.
    /// </summary>
    [Benchmark(Baseline = true, Description = "256 async pricing requests")]
    public async Task ExecutePricingWaveAsync()
    {
        var tasks = new Task<int>[RequestCount];
        for (var index = 0; index < RequestCount; index++)
        {
            tasks[index] = Batcher.ExecuteAsync(_requests[index]);
        }

        var responses = await Task.WhenAll(tasks).ConfigureAwait(false);
        if (responses.Length != RequestCount)
        {
            throw new InvalidOperationException(
                $"The batcher returned {responses.Length} responses; expected {RequestCount}.");
        }

        for (var index = 0; index < responses.Length; index++)
        {
            if (responses[index] != _requests[index])
            {
                throw new InvalidOperationException(
                    $"Response {index} was {responses[index]}; expected {_requests[index]}.");
            }
        }
    }

    /// <summary>
    /// Disposes the batcher and providers after timing has completed.
    /// </summary>
    [GlobalCleanup]
    public async Task GlobalCleanupAsync()
    {
        if (_serviceProvider is not null)
        {
            await _serviceProvider.DisposeAsync().ConfigureAwait(false);
            _serviceProvider = null;
        }

        _tracerProvider?.Dispose();
        _tracerProvider = null;
        if (Mode == ObservabilityMode.TracingAndMetrics && _exportedActivities?.Count == 0)
        {
            throw new InvalidOperationException("The tracing provider did not export any activities.");
        }

        _meterProvider?.Dispose();
        _meterProvider = null;
        if (Mode is ObservabilityMode.Metrics or ObservabilityMode.TracingAndMetrics &&
            _exportedMetrics?.Count == 0)
        {
            throw new InvalidOperationException("The metrics provider did not export any metrics.");
        }

        _exportedActivities = null;
        _exportedMetrics = null;
    }

    /// <summary>
    /// Defines the telemetry provider combinations used by the comparison.
    /// </summary>
    public enum ObservabilityMode
    {
        /// <summary>No OpenTelemetry provider is registered.</summary>
        Disabled,

        /// <summary>The RequestBatcher meter is registered without tracing.</summary>
        Metrics,

        /// <summary>The RequestBatcher meter and ActivitySource are registered.</summary>
        TracingAndMetrics,
    }

    private sealed class PricingHandler : IRequestBatchHandler<int, int>
    {
        public async ValueTask HandleAsync(
            IReadOnlyList<RequestBatchEntry<int, int>> entries,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken).ConfigureAwait(false);
            foreach (var entry in entries)
            {
                entry.SetResponse(entry.Request);
            }
        }
    }

    /// <summary>
    /// Counts exported telemetry without retaining every item across benchmark iterations.
    /// </summary>
    private sealed class CountingCollection<T> : ICollection<T>
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public bool IsReadOnly => false;

        public void Add(T item) => Interlocked.Increment(ref _count);

        public void Clear() => Volatile.Write(ref _count, 0);

        public bool Contains(T item) => false;

        public void CopyTo(T[] array, int arrayIndex)
        {
        }

        public bool Remove(T item) => false;

        public IEnumerator<T> GetEnumerator() => Enumerable.Empty<T>().GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
