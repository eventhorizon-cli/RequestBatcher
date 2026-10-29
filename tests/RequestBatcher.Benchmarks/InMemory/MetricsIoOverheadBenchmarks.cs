using System.Diagnostics;
using System.Numerics;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Engines;
using BenchmarkDotNet.Order;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using RequestBatcher.Diagnostics;

namespace RequestBatcher.Benchmarks.InMemory;

/// <summary>
/// Measures RequestBatcher metrics overhead when each handler batch waits for simulated downstream I/O.
/// </summary>
[MemoryDiagnoser]
[RankColumn]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
[SimpleJob(
    RunStrategy.Throughput,
    launchCount: 2,
    warmupCount: 4,
    iterationCount: 12,
    invocationCount: 4,
    id: "MetricsIoOverhead")]
public class MetricsIoOverheadBenchmarks
{
    private const int RequestCount = 1_024;
    private const int BatchSize = 32;
    private const int MaxConcurrency = 8;
    private const int MaxPendingRequests = 1_024;
    private const int DownstreamDelayMilliseconds = 20;
    private const string BatcherName = "pricing-queries";
    private readonly int[] _requests = Enumerable.Range(0, RequestCount).ToArray();
    private readonly int[][] _explicitGroups = CreateExplicitGroups();
    private ServiceProvider? _serviceProvider;
    private IRequestBatcher<int>? _requestBatcher;
    private IoHandler? _handler;
    private MeterProvider? _meterProvider;
    private MetricReader? _metricReader;
    private ValidatingMetricExporter? _exporter;
    private long _totalRoundTripTicks;
    private long _totalHandlerDelayTicks;
    private long _totalBatchCalls;
    private long _totalItems;
    private int _waveCount;
    private string? _benchmarkName;

    /// <summary>
    /// Selects whether metrics are disabled, aggregated only, or periodically exported.
    /// </summary>
    [Params(
        MetricsMode.Disabled,
        MetricsMode.AggregationOnly,
        MetricsMode.Periodic1000,
        MetricsMode.Periodic100)]
    public MetricsMode Mode { get; set; }

    private IRequestBatcher<int> RequestBatcher =>
        _requestBatcher ?? throw new InvalidOperationException("The benchmark has not been initialized.");

    private IoHandler Handler =>
        _handler ?? throw new InvalidOperationException("The benchmark has not been initialized.");

    /// <summary>
    /// Creates the named batcher and metric reader outside the measured region.
    /// </summary>
    [GlobalSetup]
    public void GlobalSetup()
    {
        if (Mode != MetricsMode.Disabled)
        {
            _exporter = new ValidatingMetricExporter();
            _metricReader = Mode switch
            {
                MetricsMode.AggregationOnly => new BaseExportingMetricReader(_exporter),
                MetricsMode.Periodic1000 => new PeriodicExportingMetricReader(_exporter, 1_000, 30_000),
                MetricsMode.Periodic100 => new PeriodicExportingMetricReader(_exporter, 100, 30_000),
                _ => throw new InvalidOperationException("Unknown metrics mode."),
            };
            _metricReader.TemporalityPreference = MetricReaderTemporalityPreference.Cumulative;
            _meterProvider = Sdk.CreateMeterProviderBuilder()
                // The benchmark owns a separate SDK container; supply the same sizing snapshot as its pipeline.
                .ConfigureServices(services => services.AddSingleton(new RequestBatcherMetricsConfiguration(BatchSize)))
                .AddRequestBatcherInstrumentation()
                .AddReader(_metricReader)
                .Build();
        }

        var services = new ServiceCollection();
        services.AddRequestBatcher<int, IoHandler>(
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
        _handler = _serviceProvider.GetRequiredKeyedService<IRequestBatchHandler<int>>(BatcherName) as IoHandler ??
            throw new InvalidOperationException("The metrics I/O benchmark handler was not resolved.");
        _requestBatcher = _serviceProvider.GetRequiredKeyedService<IRequestBatcher<int>>(BatcherName);
    }

    /// <summary>
    /// Submits 1,024 requests individually and waits for every request to complete.
    /// </summary>
    [Benchmark(Description = "1,024 single submissions")]
    public Task SingleSubmissionsAsync() => ExecuteWaveAsync(
        nameof(SingleSubmissionsAsync),
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
    /// Submits 1,024 requests as 32 explicit groups containing 32 requests each.
    /// </summary>
    [Benchmark(Description = "32 explicit groups of 32")]
    public Task ExplicitGroupsAsync() => ExecuteWaveAsync(
        nameof(ExplicitGroupsAsync),
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
    /// Drains the batcher, forces the final cumulative collection, and validates all four histograms.
    /// </summary>
    [GlobalCleanup]
    public async Task GlobalCleanupAsync()
    {
        if (_serviceProvider is not null)
        {
            await _serviceProvider.DisposeAsync().ConfigureAwait(false);
            _serviceProvider = null;
        }

        if (Mode != MetricsMode.Disabled)
        {
            if (_metricReader is null || _exporter is null || _meterProvider is null)
            {
                throw new InvalidOperationException("The metrics provider was not initialized.");
            }

            var exportsBeforeFinalFlush = _exporter.ExportCount;
            if (Mode is MetricsMode.Periodic1000 or MetricsMode.Periodic100 && exportsBeforeFinalFlush == 0)
            {
                throw new InvalidOperationException("Periodic metrics collection did not occur before the final flush.");
            }

            if (!_metricReader.Collect(Timeout.Infinite))
            {
                throw new InvalidOperationException("The final metrics collection did not complete.");
            }

            _meterProvider.Dispose();
            _meterProvider = null;

            var snapshot = _exporter.Snapshot();
            var expectedRequests = _waveCount * RequestCount;
            var expectedBatches = Handler.TotalBatchCalls;
            var expectedSubmissions = _waveCount * (_benchmarkName == nameof(SingleSubmissionsAsync) ? RequestCount : _explicitGroups.Length);
            if (snapshot.FirstError is not null)
            {
                throw new InvalidOperationException($"Metrics exporter failed: {snapshot.FirstError}");
            }

            if (snapshot.ObservedInstrumentCount != 4 ||
                snapshot.BatchSizeCount != expectedBatches ||
                snapshot.BatchSizeSum != expectedRequests ||
                snapshot.HandleDurationCount != expectedBatches ||
                snapshot.ExecuteDurationCount != expectedSubmissions ||
                snapshot.SubmissionSizeCount != expectedSubmissions ||
                snapshot.SubmissionSizeSum != expectedRequests)
            {
                throw new InvalidOperationException(
                    $"Metrics totals were invalid: {snapshot} ExpectedRequests={expectedRequests}, " +
                    $"ExpectedBatches={expectedBatches}.");
            }

            Console.WriteLine(
                $"Metrics validation: Benchmark={_benchmarkName}, Mode={Mode}, " +
                $"ExportsBeforeFinalFlush={exportsBeforeFinalFlush}, TotalExports={snapshot.ExportCount}, " +
                $"Instruments={snapshot.ObservedInstrumentCount}, " +
                $"BatchSizeCount={snapshot.BatchSizeCount}, BatchSizeSum={snapshot.BatchSizeSum}, " +
                $"HandleDurationCount={snapshot.HandleDurationCount}, ExecuteDurationCount={snapshot.ExecuteDurationCount}, " +
                $"SubmissionSizeCount={snapshot.SubmissionSizeCount}, SubmissionSizeSum={snapshot.SubmissionSizeSum}");
        }

        if (_waveCount > 0)
        {
            Console.WriteLine(
                $"Metrics I/O validation: Benchmark={_benchmarkName}, Mode={Mode}, " +
                $"Waves={_waveCount}, MeanActualRoundTripMilliseconds={GetMeanMilliseconds(_totalRoundTripTicks) / _waveCount:F3}, " +
                $"MeanHandlerDelayMilliseconds={GetMeanMilliseconds(_totalHandlerDelayTicks) / _totalBatchCalls:F3}, " +
                $"MeanBatchCalls={(double)_totalBatchCalls / _waveCount:F3}, " +
                $"MeanItems={(double)_totalItems / _waveCount:F3}, " +
                $"MeanItemsPerBatch={(_totalBatchCalls == 0 ? 0 : (double)_totalItems / _totalBatchCalls):F3}");
        }
    }

    private async Task ExecuteWaveAsync<TSubmission>(
        string benchmarkName,
        Func<IRequestBatcher<int>, TSubmission, Task> submit,
        TSubmission submission)
    {
        _benchmarkName = benchmarkName;
        Handler.BeginWave();
        var startedAt = Stopwatch.GetTimestamp();
        await submit(RequestBatcher, submission).ConfigureAwait(false);
        var roundTripTicks = Stopwatch.GetTimestamp() - startedAt;
        Handler.ValidateProcessed(RequestCount);

        Interlocked.Add(ref _totalRoundTripTicks, roundTripTicks);
        Interlocked.Add(ref _totalHandlerDelayTicks, Handler.WaveDelayTicks);
        Interlocked.Add(ref _totalBatchCalls, Handler.WaveBatchCalls);
        Interlocked.Add(ref _totalItems, RequestCount);
        Interlocked.Increment(ref _waveCount);
    }

    private static double GetMeanMilliseconds(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

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
    /// Controls whether metrics are disabled, aggregated only, or collected periodically.
    /// </summary>
    public enum MetricsMode
    {
        /// <summary>No OpenTelemetry meter provider is registered.</summary>
        Disabled,

        /// <summary>The SDK aggregates metrics, with collection only at cleanup.</summary>
        AggregationOnly,

        /// <summary>The SDK collects metrics every 1,000 milliseconds.</summary>
        Periodic1000,

        /// <summary>The SDK collects metrics every 100 milliseconds.</summary>
        Periodic100,
    }

    private sealed class IoHandler : IRequestBatchHandler<int>
    {
        private readonly int[] _processed = new int[RequestCount];
        private long _waveDelayTicks;
        private int _waveBatchCalls;
        private long _totalBatchCalls;

        public long WaveDelayTicks => Volatile.Read(ref _waveDelayTicks);

        public int WaveBatchCalls => Volatile.Read(ref _waveBatchCalls);

        public long TotalBatchCalls => Interlocked.Read(ref _totalBatchCalls);

        public void BeginWave()
        {
            Array.Clear(_processed);
            Volatile.Write(ref _waveDelayTicks, 0);
            Volatile.Write(ref _waveBatchCalls, 0);
        }

        public async ValueTask HandleAsync(
            IReadOnlyList<int> requests,
            CancellationToken cancellationToken = default)
        {
            var startedAt = Stopwatch.GetTimestamp();
            await Task.Delay(DownstreamDelayMilliseconds, cancellationToken).ConfigureAwait(false);
            Interlocked.Add(ref _waveDelayTicks, Stopwatch.GetTimestamp() - startedAt);
            Interlocked.Increment(ref _waveBatchCalls);
            Interlocked.Increment(ref _totalBatchCalls);

            foreach (var request in requests)
            {
                Interlocked.Increment(ref _processed[request]);
            }
        }

        public void ValidateProcessed(int expected)
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

            if (total != expected)
            {
                throw new InvalidOperationException(
                    $"The handler processed {total} requests; expected {expected}.");
            }
        }
    }

    private sealed class ValidatingMetricExporter : BaseExporter<Metric>
    {
        private readonly object _gate = new();
        private int _exportCount;
        private int _observedInstrumentCount;
        private long _batchSizeCount;
        private long _batchSizeSum;
        private long _handleDurationCount;
        private long _executeDurationCount;
        private long _submissionSizeCount;
        private long _submissionSizeSum;
        private string? _firstError;

        public int ExportCount
        {
            get
            {
                lock (_gate)
                {
                    return _exportCount;
                }
            }
        }

        public override ExportResult Export(in Batch<Metric> batch)
        {
            try
            {
                lock (_gate)
                {
                    foreach (var metric in batch)
                    {
                        switch (metric.Name)
                        {
                            case "requestbatcher.batch.size":
                                _observedInstrumentCount |= 1;
                                _batchSizeCount = 0;
                                _batchSizeSum = 0;
                                foreach (var point in metric.GetMetricPoints())
                                {
                                    _batchSizeCount += point.GetHistogramCount();
                                    _batchSizeSum += (long)point.GetHistogramSum();
                                }

                                break;
                            case "requestbatcher.handle.duration":
                                _observedInstrumentCount |= 2;
                                _handleDurationCount = 0;
                                foreach (var point in metric.GetMetricPoints())
                                {
                                    _handleDurationCount += point.GetHistogramCount();
                                }

                                break;
                            case "requestbatcher.execute.duration":
                                _observedInstrumentCount |= 4;
                                _executeDurationCount = 0;
                                foreach (var point in metric.GetMetricPoints())
                                {
                                    _executeDurationCount += point.GetHistogramCount();
                                }

                                break;
                            case "requestbatcher.submission.size":
                                _observedInstrumentCount |= 8;
                                _submissionSizeCount = 0;
                                _submissionSizeSum = 0;
                                foreach (var point in metric.GetMetricPoints())
                                {
                                    _submissionSizeCount += point.GetHistogramCount();
                                    _submissionSizeSum += (long)point.GetHistogramSum();
                                }

                                break;
                        }
                    }

                    _exportCount++;
                }

                return ExportResult.Success;
            }
            catch (Exception exception)
            {
                lock (_gate)
                {
                    _firstError ??= exception.ToString();
                }

                return ExportResult.Failure;
            }
        }

        public MetricSnapshot Snapshot()
        {
            lock (_gate)
            {
                return new MetricSnapshot(
                    _exportCount,
                    BitOperations.PopCount((uint)_observedInstrumentCount),
                    _batchSizeCount,
                    _batchSizeSum,
                    _handleDurationCount,
                    _executeDurationCount,
                    _submissionSizeCount,
                    _submissionSizeSum,
                    _firstError);
            }
        }
    }

    private readonly record struct MetricSnapshot(
        int ExportCount,
        int ObservedInstrumentCount,
        long BatchSizeCount,
        long BatchSizeSum,
        long HandleDurationCount,
        long ExecuteDurationCount,
        long SubmissionSizeCount,
        long SubmissionSizeSum,
        string? FirstError);
}
