using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;

namespace RequestBatcher.Benchmarks.InMemory;

/// <summary>
/// Measures the complete batching pipeline against bounded direct calls to an in-memory delayed downstream.
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class DelayDownstreamOverheadBenchmarks
{
    private const int DelayMilliseconds = 50;
    private ServiceProvider? _provider;
    private IRequestBatcher<int>? _batcher;
    private DelayedDownstream _downstream = null!;
    private int[] _requests = [];
    private int _nextBatch;
    private int _waveCount;
    private long _totalCalls;
    private int _minimumCalls = int.MaxValue;
    private int _maximumCalls;
    private int _maximumConcurrency;

    [Params(1, 100)]
    public int BatchSize { get; set; }

    [Params(4, 16)]
    public int MaxConcurrency { get; set; }

    private int PartitionCount => BatchSize == 1 ? 1 : 8;

    [GlobalSetup]
    public async Task SetupAsync()
    {
        _requests = Enumerable.Range(0, BatchSize * MaxConcurrency * 2).ToArray();
        _downstream = new DelayedDownstream(_requests.Length, MaxConcurrency);
        var services = new ServiceCollection();
        services.AddSingleton(_downstream);
        services.AddRequestBatcher<int, DelayHandler>(ServiceLifetime.Scoped, options =>
        {
            options.BatchSize = BatchSize;
            options.MaxConcurrency = MaxConcurrency;
            options.MaxPendingRequests = _requests.Length;
            if (PartitionCount > 1)
            {
                options.UsePartitionKey(static request => request % 8, PartitionCount);
            }
        });
        _provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
        _batcher = _provider.GetRequiredService<IRequestBatcher<int>>();
        await DirectAsync().ConfigureAwait(false);
        await ViaRequestBatcherAsync().ConfigureAwait(false);
        _waveCount = 0;
        _totalCalls = 0;
        _minimumCalls = int.MaxValue;
        _maximumCalls = 0;
        _maximumConcurrency = 0;
    }

    [Benchmark(Baseline = true, Description = "Bounded direct downstream calls")]
    public async Task DirectAsync()
    {
        _downstream.BeginWave();
        _nextBatch = -1;
        var workers = new Task[MaxConcurrency];
        for (var index = 0; index < workers.Length; index++)
        {
            workers[index] = RunDirectWorkerAsync();
        }

        await Task.WhenAll(workers).ConfigureAwait(false);
        ValidateWave(requireExactBatchCount: true);
    }

    [Benchmark(Description = "RequestBatcher explicit submission")]
    public async Task ViaRequestBatcherAsync()
    {
        _downstream.BeginWave();
        await _batcher!.ExecuteAsync(_requests).ConfigureAwait(false);
        ValidateWave(requireExactBatchCount: BatchSize == 1);
    }

    [GlobalCleanup]
    public async Task CleanupAsync()
    {
        if (_provider is not null)
        {
            await _provider.DisposeAsync().ConfigureAwait(false);
        }

        Console.WriteLine(
            $"Downstream validation: RequestCount={_requests.Length}, PartitionCount={PartitionCount}, " +
            $"Waves={_waveCount}, CallsPerWave={_minimumCalls}..{_maximumCalls}, " +
            $"MeanEffectiveBatchSize={(_waveCount == 0 ? 0 : (double)_requests.Length * _waveCount / _totalCalls):F3}, " +
            $"PeakConcurrency={_maximumConcurrency}, DelayMilliseconds={DelayMilliseconds}");
    }

    private async Task RunDirectWorkerAsync()
    {
        while (true)
        {
            var offset = Interlocked.Increment(ref _nextBatch) * BatchSize;
            if (offset >= _requests.Length)
            {
                return;
            }

            await _downstream.InvokeAsync(new ArraySegment<int>(_requests, offset, BatchSize))
                .ConfigureAwait(false);
        }
    }

    private void ValidateWave(bool requireExactBatchCount)
    {
        _downstream.Validate();
        var calls = _downstream.CallCount;
        if (requireExactBatchCount && calls != MaxConcurrency * 2)
        {
            throw new InvalidOperationException($"Expected {MaxConcurrency * 2} calls, received {calls}.");
        }

        _waveCount++;
        _totalCalls += calls;
        _minimumCalls = Math.Min(_minimumCalls, calls);
        _maximumCalls = Math.Max(_maximumCalls, calls);
        _maximumConcurrency = Math.Max(_maximumConcurrency, _downstream.PeakConcurrency);
    }

    private sealed class DelayHandler(DelayedDownstream downstream) : IRequestBatchHandler<int>
    {
        public ValueTask HandleAsync(IReadOnlyList<int> requests, CancellationToken cancellationToken = default) =>
            downstream.InvokeAsync(requests, cancellationToken);
    }

    private sealed class DelayedDownstream(int requestCount, int maxConcurrency)
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

        public void Validate()
        {
            if (_active != 0 || _peak > maxConcurrency)
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
