using System.Diagnostics;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using Microsoft.Extensions.DependencyInjection;

namespace RequestBatcher.Benchmarks.InMemory;

/// <summary>
/// Compares the complete batching path with direct, fully packed handler batches.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Throughput, launchCount: 2, warmupCount: 3, iterationCount: 8,
    invocationCount: 4, id: "DispatchCapacity")]
public class DispatchCapacityBenchmarks
{
    private const int RequestCount = 4_096;
    private const int BatchSize = 32;
    private readonly int[] _requests = Enumerable.Range(0, RequestCount).ToArray();
    private readonly ArraySegment<int>[] _batches = Enumerable.Range(0, RequestCount / BatchSize)
        .Select(index => new ArraySegment<int>(Enumerable.Range(index * BatchSize, BatchSize).ToArray())).ToArray();
    private ServiceProvider _provider = null!;
    private IRequestBatcher<int> _batcher = null!;
    private ProbeHandler _handler = null!;
    private int _nextBatch;
    private int _waves;
    private long _totalTicks;
    private long _handlerTicks;
    private long _calls;
    private int _peak;
    private int _minCalls = int.MaxValue;
    private int _maxCalls;

    /// <summary>Maximum concurrent handler invocations.</summary>
    [Params(8, 32)]
    public int Concurrency { get; set; }

    /// <summary>One unkeyed partition or sixteen partitions with integer keys.</summary>
    [Params(1, 16)]
    public int Partitions { get; set; }

    /// <summary>Synchronous completion or simulated asynchronous downstream I/O.</summary>
    [Params(0, 20)]
    public int DelayMilliseconds { get; set; }

    /// <summary>Creates the batcher outside measurement without telemetry listeners.</summary>
    [GlobalSetup]
    public void Setup()
    {
        var services = new ServiceCollection();
        services.AddRequestBatcher<int, ProbeHandler>(ServiceLifetime.Singleton, options =>
        {
            options.BatchSize = BatchSize;
            options.MaxConcurrency = Concurrency;
            options.MaxPendingRequests = RequestCount;
            if (Partitions > 1)
            {
                options.UsePartitionKey(static request => request % 256, Partitions);
            }
        });
        _provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
        _handler = (ProbeHandler)_provider.GetRequiredService<IRequestBatchHandler<int>>();
        _handler.DelayMilliseconds = DelayMilliseconds;
        _batcher = _provider.GetRequiredService<IRequestBatcher<int>>();
    }

    /// <summary>Runs full handler batches through a fixed number of async workers without a queue.</summary>
    [Benchmark(Baseline = true)]
    public async Task DirectFullBatchesAsync()
    {
        _handler.Reset();
        _nextBatch = -1;
        var started = Stopwatch.GetTimestamp();
        var workers = new Task[Concurrency];
        for (var index = 0; index < workers.Length; index++)
        {
            workers[index] = RunWorkerAsync();
        }

        await Task.WhenAll(workers).ConfigureAwait(false);
        ValidateAndRecord(started, direct: true);
    }

    /// <summary>Submits the same request set through the public explicit-submission API.</summary>
    [Benchmark]
    public async Task RequestBatcherAsync()
    {
        _handler.Reset();
        var started = Stopwatch.GetTimestamp();
        await _batcher.ExecuteAsync(_requests).ConfigureAwait(false);
        ValidateAndRecord(started, direct: false);
    }

    /// <summary>Disposes the batcher and reports observed batch formation and handler occupancy.</summary>
    [GlobalCleanup]
    public async Task CleanupAsync()
    {
        await _provider.DisposeAsync().ConfigureAwait(false);
        Console.WriteLine(
            $"Dispatch validation: Concurrency={Concurrency}, Partitions={Partitions}, DelayMs={DelayMilliseconds}, " +
            $"Waves={_waves}, RequestsPerWave={RequestCount}, CallsPerWave={_minCalls}..{_maxCalls}, " +
            $"MeanBatchSize={(double)RequestCount * _waves / _calls:F3}, Peak={_peak}, " +
            $"MeanHandlerMs={_handlerTicks * 1000.0 / Stopwatch.Frequency / _calls:F6}, " +
            $"MeanWaveMs={_totalTicks * 1000.0 / Stopwatch.Frequency / _waves:F6}, " +
            $"HandlerOccupancy={(double)_handlerTicks / _totalTicks / Concurrency:P3}, ExactlyOnce=True");
    }

    private async Task RunWorkerAsync()
    {
        int index;
        while ((index = Interlocked.Increment(ref _nextBatch)) < _batches.Length)
        {
            await _handler.HandleAsync(_batches[index]).ConfigureAwait(false);
        }
    }

    private void ValidateAndRecord(long started, bool direct)
    {
        var elapsed = Stopwatch.GetTimestamp() - started;
        _handler.Validate(Concurrency);
        if (direct && _handler.Calls != _batches.Length)
        {
            throw new InvalidOperationException("The direct baseline did not execute all full batches.");
        }

        _waves++;
        _totalTicks += elapsed;
        _handlerTicks += _handler.ElapsedTicks;
        _calls += _handler.Calls;
        _peak = Math.Max(_peak, _handler.Peak);
        _minCalls = Math.Min(_minCalls, _handler.Calls);
        _maxCalls = Math.Max(_maxCalls, _handler.Calls);
    }

    private sealed class ProbeHandler : IRequestBatchHandler<int>
    {
        private readonly int[] _seen = new int[RequestCount];
        private int _active;
        private int _peak;
        private int _calls;
        private long _elapsedTicks;

        public int Peak => _peak;
        public int Calls => _calls;
        public long ElapsedTicks => _elapsedTicks;
        public int DelayMilliseconds { get; set; }

        public void Reset()
        {
            Array.Clear(_seen);
            _peak = 0;
            _calls = 0;
            _elapsedTicks = 0;
        }

        public async ValueTask HandleAsync(IReadOnlyList<int> requests, CancellationToken cancellationToken = default)
        {
            if (requests.Count is < 1 or > BatchSize)
            {
                throw new InvalidOperationException("Invalid handler batch size.");
            }

            var active = Interlocked.Increment(ref _active);
            var peak = Volatile.Read(ref _peak);
            while (active > peak)
            {
                var previous = Interlocked.CompareExchange(ref _peak, active, peak);
                if (previous == peak)
                {
                    break;
                }

                peak = previous;
            }

            Interlocked.Increment(ref _calls);
            var started = Stopwatch.GetTimestamp();
            try
            {
                if (DelayMilliseconds > 0)
                {
                    await Task.Delay(DelayMilliseconds, cancellationToken).ConfigureAwait(false);
                }

                for (var index = 0; index < requests.Count; index++)
                {
                    Interlocked.Increment(ref _seen[requests[index]]);
                }
            }
            finally
            {
                Interlocked.Add(ref _elapsedTicks, Stopwatch.GetTimestamp() - started);
                Interlocked.Decrement(ref _active);
            }
        }

        public void Validate(int concurrency)
        {
            if (_active != 0 || _peak > concurrency || _seen.Any(static count => count != 1))
            {
                throw new InvalidOperationException("Requests must complete exactly once within the concurrency limit.");
            }
        }
    }
}
