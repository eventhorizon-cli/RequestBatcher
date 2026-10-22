using System.Diagnostics;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Engines;
using BenchmarkDotNet.Order;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Trace;

namespace RequestBatcher.Benchmarks.InMemory;

/// <summary>
/// Measures tracing overhead when every handler batch waits for a simulated downstream I/O operation.
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
    id: "TracingIoOverhead")]
public class TracingIoOverheadBenchmarks
{
    private const int RequestCount = 1_024;
    private const int BatchSize = 32;
    private const int MaxConcurrency = 8;
    private const int MaxPendingRequests = 1_024;
    private const int DownstreamDelayMilliseconds = 20;
    private readonly int[] _requests = Enumerable.Range(0, RequestCount).ToArray();
    private readonly int[][] _explicitGroups = CreateExplicitGroups();
    private ServiceProvider? _serviceProvider;
    private IRequestBatcher<int>? _requestBatcher;
    private IoHandler? _handler;
    private TracerProvider? _tracerProvider;
    private CountingSampler? _sampler;
    private long _totalRoundTripTicks;
    private long _totalHandlerDelayTicks;
    private long _totalBatchCalls;
    private long _totalItems;
    private int _waveCount;
    private int _stoppedActivityCount;
    private string? _benchmarkName;

    /// <summary>
    /// Selects whether tracing is disabled or an SDK root sampling rate is configured.
    /// </summary>
    [Params(ListenerMode.Disabled, ListenerMode.Sample0, ListenerMode.Sample1, ListenerMode.Sample10, ListenerMode.Sample100)]
    public ListenerMode TracingMode { get; set; }

    private IRequestBatcher<int> RequestBatcher =>
        _requestBatcher ?? throw new InvalidOperationException("The benchmark has not been initialized.");

    private IoHandler Handler =>
        _handler ?? throw new InvalidOperationException("The benchmark has not been initialized.");

    /// <summary>
    /// Configures the SDK sampler and dependency injection graph before measurement starts.
    /// </summary>
    [GlobalSetup]
    public void GlobalSetup()
    {
        if (TracingMode != ListenerMode.Disabled)
        {
            var ratio = TracingMode switch
            {
                ListenerMode.Sample0 => 0.0,
                ListenerMode.Sample1 => 0.01,
                ListenerMode.Sample10 => 0.1,
                ListenerMode.Sample100 => 1.0,
                _ => throw new InvalidOperationException("Unknown sampling mode."),
            };
            _sampler = new CountingSampler(new ParentBasedSampler(new TraceIdRatioBasedSampler(ratio)));
            _tracerProvider = Sdk.CreateTracerProviderBuilder()
                .AddSource("RequestBatcher")
                .SetSampler(_sampler)
                .AddProcessor(new CountingProcessor(() => Interlocked.Increment(ref _stoppedActivityCount)))
                .Build();
        }

        var services = new ServiceCollection();
        services.AddRequestBatcher<int, IoHandler>(
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
        _handler = _serviceProvider.GetRequiredService<IRequestBatchHandler<int>>() as IoHandler ??
            throw new InvalidOperationException("The tracing I/O benchmark handler was not resolved.");
        _requestBatcher = _serviceProvider.GetRequiredService<IRequestBatcher<int>>();
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
    /// Prints aggregate diagnostics after all benchmark invocations and disposes the pipeline.
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

        var decisions = _sampler?.Decisions ?? 0;
        var sampled = _sampler?.Sampled ?? 0;
        if (TracingMode != ListenerMode.Disabled && decisions == 0)
        {
            throw new InvalidOperationException("The SDK sampler did not observe any RequestBatcher spans.");
        }

        if (sampled != _stoppedActivityCount
            || (TracingMode == ListenerMode.Sample0 && sampled != 0)
            || (TracingMode == ListenerMode.Sample100 && sampled != decisions)
            || (TracingMode is ListenerMode.Sample1 or ListenerMode.Sample10 && (sampled == 0 || sampled == decisions)))
        {
            throw new InvalidOperationException("Sampling decisions did not match completed sampled spans or the configured rate.");
        }

        if (_waveCount > 0)
        {
            Console.WriteLine(
                $"Tracing I/O validation: Benchmark={_benchmarkName}, ListenerMode={TracingMode}, " +
                $"Waves={_waveCount}, MeanActualRoundTripMilliseconds={GetMeanMilliseconds(_totalRoundTripTicks) / _waveCount:F3}, " +
                $"MeanHandlerDelayMilliseconds={GetMeanMilliseconds(_totalHandlerDelayTicks) / _totalBatchCalls:F3}, " +
                $"MeanBatchCalls={(double)_totalBatchCalls / _waveCount:F3}, " +
                $"MeanItems={(double)_totalItems / _waveCount:F3}, " +
                $"MeanItemsPerBatch={(_totalBatchCalls == 0 ? 0 : (double)_totalItems / _totalBatchCalls):F3}, " +
                $"SamplingDecisions={decisions}, SampledSpans={_stoppedActivityCount}, " +
                $"ObservedSamplingPercent={(decisions == 0 ? 0 : 100.0 * sampled / decisions):F3}");
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
    /// Controls whether the RequestBatcher Activity source is listened to.
    /// </summary>
    public enum ListenerMode
    {
        /// <summary>No ActivityListener is installed.</summary>
        Disabled,

        /// <summary>The SDK drops all root spans while maintaining propagation context.</summary>
        Sample0,

        /// <summary>The SDK samples approximately one percent of root traces.</summary>
        Sample1,

        /// <summary>The SDK samples approximately ten percent of root traces.</summary>
        Sample10,

        /// <summary>The SDK records every root trace without exporting or retaining spans.</summary>
        Sample100,
    }

    private sealed class CountingSampler(Sampler inner) : Sampler
    {
        private long _decisions;
        private long _sampled;

        public long Decisions => Interlocked.Read(ref _decisions);

        public long Sampled => Interlocked.Read(ref _sampled);

        public override SamplingResult ShouldSample(in SamplingParameters samplingParameters)
        {
            var result = inner.ShouldSample(in samplingParameters);
            Interlocked.Increment(ref _decisions);
            if (result.Decision == SamplingDecision.RecordAndSample)
            {
                Interlocked.Increment(ref _sampled);
            }

            return result;
        }
    }

    private sealed class CountingProcessor(Action stopped) : BaseProcessor<Activity>
    {
        public override void OnEnd(Activity activity) => stopped();
    }

    private sealed class IoHandler : IRequestBatchHandler<int>
    {
        private readonly int[] _processed = new int[RequestCount];
        private long _waveDelayTicks;
        private int _waveBatchCalls;

        public long WaveDelayTicks => Volatile.Read(ref _waveDelayTicks);

        public int WaveBatchCalls => Volatile.Read(ref _waveBatchCalls);

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
}
