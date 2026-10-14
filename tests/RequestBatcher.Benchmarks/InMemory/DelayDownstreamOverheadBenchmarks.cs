using System.Diagnostics;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using Microsoft.Extensions.DependencyInjection;

namespace RequestBatcher.Benchmarks.InMemory;

/// <summary>
/// Compares direct and batched downstream calls under paced arrivals or optional burst traffic.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Monitoring, launchCount: 1, warmupCount: 1, iterationCount: 3, invocationCount: 1)]
public class DelayDownstreamOverheadBenchmarks
{
    private const int BatchSize = 100;
    private const int MaxConcurrency = 16;
    private const int PartitionCount = 8;
    private const int DelayMilliseconds = 50;
    private const int CallerCount = 8;
    private const int TargetItemsPerSecond = 16_000;
    private ServiceProvider? _provider;
    private IRequestBatcher<int> _batcher = null!;
    private DelayedDownstream _downstream = null!;
    private int[] _requests = [];
    private int _nextBatch;
    private int _waveCount;
    private long _totalCalls;
    private int _minimumCalls = int.MaxValue;
    private int _maximumCalls;
    private int _maximumConcurrency;
    private double _totalIssueMilliseconds;
    private double _minimumIssueMilliseconds = double.MaxValue;
    private double _maximumIssueMilliseconds;
    private double _lastIssueMilliseconds;
    private SubmissionMode _lastMode;
    private double[]? _diagnosticLatencies;
    private int _diagnosticCount;

    [ParamsSource(nameof(RequestCounts))]
    public int RequestCount { get; set; }

    public IEnumerable<int> RequestCounts => IsBurst ? [16_000, 64_000] : [16_000];

    private static string TrafficProfile => Environment.GetEnvironmentVariable("REQUEST_BATCHER_TRAFFIC") switch
    {
        null or "" or "steady" => "steady",
        "burst" => "burst",
        var value => throw new InvalidOperationException($"Unknown REQUEST_BATCHER_TRAFFIC value: {value}."),
    };

    private static bool IsBurst => TrafficProfile == "burst";

    [GlobalSetup]
    public void Setup()
    {
        _requests = Enumerable.Range(0, RequestCount).ToArray();
        _downstream = new DelayedDownstream(RequestCount);
        var services = new ServiceCollection();
        services.AddSingleton(_downstream);
        services.AddRequestBatcher<int, DelayHandler>(ServiceLifetime.Scoped, options =>
        {
            options.BatchSize = BatchSize;
            options.MaxConcurrency = MaxConcurrency;
            options.MaxPendingRequests = RequestCount;
            options.UsePartitionKey(static request => request % PartitionCount, PartitionCount);
        });
        _provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
        _batcher = _provider.GetRequiredService<IRequestBatcher<int>>();
        Console.WriteLine(
            $"Traffic profile: {TrafficProfile}, RequestCount={RequestCount}, " +
            $"Callers={(IsBurst ? 1 : CallerCount)}, TargetItemsPerSecond={(IsBurst ? 0 : TargetItemsPerSecond)}");
    }

    [Benchmark(Description = "Direct single, unbounded")]
    public Task DirectSingleUnboundedAsync() => RunWaveAsync(SubmissionMode.DirectSingle);

    [Benchmark(Description = "Direct batch, unbounded")]
    public Task DirectBatchUnboundedAsync() => RunWaveAsync(SubmissionMode.DirectBatch);

    [Benchmark(Baseline = true, Description = "Direct batch, concurrency 16")]
    public Task DirectBatchBoundedAsync() => RunWaveAsync(SubmissionMode.DirectBatchBounded);

    [Benchmark(Description = "RequestBatcher single")]
    public Task ViaRequestBatcherSingleAsync() => RunWaveAsync(SubmissionMode.LibrarySingle);

    [Benchmark(Description = "RequestBatcher batch")]
    public Task ViaRequestBatcherBatchAsync() => RunWaveAsync(SubmissionMode.LibraryBatch);

    [GlobalCleanup]
    public async Task CleanupAsync()
    {
        try
        {
            Console.WriteLine(
                $"Downstream validation: Profile={TrafficProfile}, RequestCount={RequestCount}, PartitionCount={PartitionCount}, " +
                $"Waves={_waveCount}, CallsPerWave={_minimumCalls}..{_maximumCalls}, " +
                $"MeanEffectiveBatchSize={(_waveCount == 0 ? 0 : (double)RequestCount * _waveCount / _totalCalls):F3}, " +
                $"PeakConcurrency={_maximumConcurrency}, DelayMilliseconds={DelayMilliseconds}");
            if (!IsBurst && _waveCount > 0)
            {
                var meanIssueMilliseconds = _totalIssueMilliseconds / _waveCount;
                Console.WriteLine(
                    $"Arrival validation: TargetItemsPerSecond={TargetItemsPerSecond}, " +
                    $"IssueMilliseconds={_minimumIssueMilliseconds:F3}..{_maximumIssueMilliseconds:F3}, " +
                    $"MeanIssueMilliseconds={meanIssueMilliseconds:F3}, " +
                    $"MeanIssuedItemsPerSecond={RequestCount * 1000.0 / meanIssueMilliseconds:F1}");
            }

            if (_waveCount > 0)
            {
                await DiagnoseLatencyAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            if (_provider is not null)
            {
                await _provider.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task DiagnoseLatencyAsync()
    {
        var submissions = _lastMode is SubmissionMode.DirectSingle or SubmissionMode.LibrarySingle
            ? RequestCount
            : IsBurst && _lastMode == SubmissionMode.LibraryBatch ? 1 : RequestCount / BatchSize;
        _diagnosticLatencies = new double[submissions];
        _diagnosticCount = 0;
        await RunWaveAsync(_lastMode).ConfigureAwait(false);
        if (_diagnosticCount != submissions)
        {
            throw new InvalidOperationException($"Expected {submissions} latency observations, received {_diagnosticCount}.");
        }

        Array.Sort(_diagnosticLatencies);
        Console.WriteLine(
            $"Diagnostic submission latency (one separate wave, outside measurements): Profile={TrafficProfile}, " +
            $"Submissions={submissions}, P50Milliseconds={GetLatencyPercentile(0.50):F3}, " +
            $"P95Milliseconds={GetLatencyPercentile(0.95):F3}, P99Milliseconds={GetLatencyPercentile(0.99):F3}, " +
            $"Calls={_downstream.CallCount}, MeanEffectiveBatchSize={(double)RequestCount / _downstream.CallCount:F3}, " +
            $"PeakConcurrency={_downstream.PeakConcurrency}");
        if (!IsBurst)
        {
            Console.WriteLine(
                $"Diagnostic arrivals: IssueMilliseconds={_lastIssueMilliseconds:F3}, " +
                $"IssuedItemsPerSecond={RequestCount * 1000.0 / _lastIssueMilliseconds:F1}");
        }
    }

    private double GetLatencyPercentile(double percentile)
    {
        var latencies = _diagnosticLatencies!;
        return latencies[(int)Math.Ceiling(latencies.Length * percentile) - 1];
    }

    private async Task RunWaveAsync(SubmissionMode mode)
    {
        _lastMode = mode;
        _downstream.BeginWave();
        if (IsBurst)
        {
            await RunBurstAsync(mode).ConfigureAwait(false);
        }
        else
        {
            await RunSteadyAsync(mode).ConfigureAwait(false);
        }

        var expectedCalls = mode switch
        {
            SubmissionMode.DirectSingle => RequestCount,
            SubmissionMode.DirectBatch or SubmissionMode.DirectBatchBounded => RequestCount / BatchSize,
            _ => (int?)null,
        };
        int? concurrencyLimit = mode is SubmissionMode.DirectSingle or SubmissionMode.DirectBatch
            ? null
            : MaxConcurrency;
        ValidateWave(expectedCalls, concurrencyLimit);
    }

    private async Task RunSteadyAsync(SubmissionMode mode)
    {
        using var gate = mode == SubmissionMode.DirectBatchBounded ? new SemaphoreSlim(MaxConcurrency) : null;
        var callers = new Task[CallerCount];
        var issueWindows = new IssueWindow[CallerCount];
        var startedAt = Stopwatch.GetTimestamp();
        for (var caller = 0; caller < callers.Length; caller++)
        {
            callers[caller] = RunCallerAsync(caller, startedAt, mode, gate, issueWindows);
        }

        await Task.WhenAll(callers).ConfigureAwait(false);
        var firstIssue = issueWindows.Min(static window => window.First);
        var lastIssue = issueWindows.Max(static window => window.Last);
        _lastIssueMilliseconds = Stopwatch.GetElapsedTime(firstIssue, lastIssue).TotalMilliseconds;
        if (_diagnosticLatencies is null)
        {
            _totalIssueMilliseconds += _lastIssueMilliseconds;
            _minimumIssueMilliseconds = Math.Min(_minimumIssueMilliseconds, _lastIssueMilliseconds);
            _maximumIssueMilliseconds = Math.Max(_maximumIssueMilliseconds, _lastIssueMilliseconds);
        }
    }

    private async Task RunCallerAsync(
        int caller,
        long startedAt,
        SubmissionMode mode,
        SemaphoreSlim? gate,
        IssueWindow[] issueWindows)
    {
        var itemsPerCall = mode is SubmissionMode.DirectSingle or SubmissionMode.LibrarySingle ? 1 : BatchSize;
        var itemsPerCaller = RequestCount / CallerCount;
        var tasks = new Task[itemsPerCaller / itemsPerCall];
        long firstIssue = 0;
        long lastIssue = 0;
        for (var index = 0; index < tasks.Length; index++)
        {
            // Stagger callers within each interval and retain absolute deadlines when the timer runs late.
            var scheduledItems = ((long)index * CallerCount + caller) * itemsPerCall;
            var deadline = startedAt + (long)(scheduledItems * (double)Stopwatch.Frequency / TargetItemsPerSecond);
            await WaitUntilAsync(deadline).ConfigureAwait(false);
            if (index == 0)
            {
                firstIssue = Stopwatch.GetTimestamp();
            }

            if (index == tasks.Length - 1)
            {
                lastIssue = Stopwatch.GetTimestamp();
            }

            var offset = caller * itemsPerCaller + index * itemsPerCall;
            tasks[index] = SubmitAsync(mode, offset, itemsPerCall, gate);
        }

        issueWindows[caller] = new IssueWindow(firstIssue, lastIssue);
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private static async ValueTask WaitUntilAsync(long deadline)
    {
        long remainingTicks;
        while ((remainingTicks = deadline - Stopwatch.GetTimestamp()) > 0)
        {
            var milliseconds = Math.Max(1, Math.Ceiling(remainingTicks * 1000.0 / Stopwatch.Frequency));
            await Task.Delay(TimeSpan.FromMilliseconds(milliseconds)).ConfigureAwait(false);
        }
    }

    private Task SubmitAsync(SubmissionMode mode, int offset, int count, SemaphoreSlim? gate = null)
    {
        var startedAt = _diagnosticLatencies is null ? 0 : Stopwatch.GetTimestamp();
        var submission = mode switch
        {
            SubmissionMode.LibrarySingle => _batcher.ExecuteAsync(_requests[offset]),
            SubmissionMode.LibraryBatch => _batcher.ExecuteAsync(new ArraySegment<int>(_requests, offset, count)),
            SubmissionMode.DirectBatchBounded => InvokeBoundedAsync(new ArraySegment<int>(_requests, offset, count), gate!),
            _ => _downstream.InvokeAsync(new ArraySegment<int>(_requests, offset, count)).AsTask(),
        };
        return _diagnosticLatencies is null ? submission : ObserveLatencyAsync(submission, startedAt);
    }

    private async Task ObserveLatencyAsync(Task submission, long startedAt)
    {
        try
        {
            await submission.ConfigureAwait(false);
        }
        finally
        {
            var elapsedMilliseconds = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
            var index = Interlocked.Increment(ref _diagnosticCount) - 1;
            _diagnosticLatencies![index] = elapsedMilliseconds;
        }
    }

    private async Task InvokeBoundedAsync(IReadOnlyList<int> requests, SemaphoreSlim gate)
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await _downstream.InvokeAsync(requests).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task RunBurstAsync(SubmissionMode mode)
    {
        if (mode == SubmissionMode.LibraryBatch)
        {
            var startedAt = _diagnosticLatencies is null ? 0 : Stopwatch.GetTimestamp();
            var submission = _batcher.ExecuteAsync(_requests);
            await (_diagnosticLatencies is null ? submission : ObserveLatencyAsync(submission, startedAt))
                .ConfigureAwait(false);
            return;
        }

        if (mode == SubmissionMode.DirectBatchBounded)
        {
            _nextBatch = -1;
            var workers = new Task[MaxConcurrency];
            for (var index = 0; index < workers.Length; index++)
            {
                workers[index] = RunDirectWorkerAsync();
            }

            await Task.WhenAll(workers).ConfigureAwait(false);
            return;
        }

        var itemsPerCall = mode == SubmissionMode.DirectBatch ? BatchSize : 1;
        var tasks = new Task[RequestCount / itemsPerCall];
        for (var index = 0; index < tasks.Length; index++)
        {
            tasks[index] = SubmitAsync(mode, index * itemsPerCall, itemsPerCall);
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task RunDirectWorkerAsync()
    {
        while (true)
        {
            var offset = Interlocked.Increment(ref _nextBatch) * BatchSize;
            if (offset >= RequestCount)
            {
                return;
            }

            await SubmitAsync(SubmissionMode.DirectBatch, offset, BatchSize).ConfigureAwait(false);
        }
    }

    private void ValidateWave(int? expectedCalls, int? concurrencyLimit)
    {
        _downstream.Validate(concurrencyLimit);
        var calls = _downstream.CallCount;
        if (expectedCalls is not null && calls != expectedCalls)
        {
            throw new InvalidOperationException($"Expected {expectedCalls} calls, received {calls}.");
        }

        if (_diagnosticLatencies is not null)
        {
            return;
        }

        _waveCount++;
        _totalCalls += calls;
        _minimumCalls = Math.Min(_minimumCalls, calls);
        _maximumCalls = Math.Max(_maximumCalls, calls);
        _maximumConcurrency = Math.Max(_maximumConcurrency, _downstream.PeakConcurrency);
    }

    private enum SubmissionMode
    {
        DirectSingle,
        DirectBatch,
        DirectBatchBounded,
        LibrarySingle,
        LibraryBatch,
    }

    private readonly record struct IssueWindow(long First, long Last);

    private sealed class DelayHandler(DelayedDownstream downstream) : IRequestBatchHandler<int>
    {
        public ValueTask HandleAsync(IReadOnlyList<int> requests, CancellationToken cancellationToken = default) =>
            downstream.InvokeAsync(requests, cancellationToken);
    }

    private sealed class DelayedDownstream(int requestCount)
    {
        private readonly int[] _processed = new int[requestCount];
        private int _active;
        private int _calls;
        private int _peak;

        public int CallCount => _calls;
        public int PeakConcurrency => _peak;

        public void BeginWave()
        {
            Array.Clear(_processed);
            _calls = 0;
            _peak = 0;
        }

        public async ValueTask InvokeAsync(IReadOnlyList<int> requests, CancellationToken cancellationToken = default)
        {
            var active = Interlocked.Increment(ref _active);
            Interlocked.Increment(ref _calls);
            int peak;
            do
            {
                peak = Volatile.Read(ref _peak);
            }
            while (peak < active && Interlocked.CompareExchange(ref _peak, active, peak) != peak);

            try
            {
                await Task.Delay(DelayMilliseconds, cancellationToken).ConfigureAwait(false);
                for (var index = 0; index < requests.Count; index++)
                {
                    Interlocked.Increment(ref _processed[requests[index]]);
                }
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }

        public void Validate(int? concurrencyLimit)
        {
            if (_active != 0 || (concurrencyLimit is not null && _peak > concurrencyLimit))
            {
                throw new InvalidOperationException($"Invalid concurrency: active={_active}, peak={_peak}.");
            }

            for (var index = 0; index < _processed.Length; index++)
            {
                if (_processed[index] != 1)
                {
                    throw new InvalidOperationException($"Request {index} completed {_processed[index]} times.");
                }
            }
        }
    }
}
