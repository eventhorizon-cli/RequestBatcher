using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using Microsoft.Extensions.DependencyInjection;

namespace RequestBatcher.Benchmarks.InMemory;

/// <summary>
/// Compares a typed singleton handler with a static delegate for request-only work.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(
    RunStrategy.Throughput,
    launchCount: 1,
    warmupCount: 3,
    iterationCount: 8,
    id: "DelegateRequestOnly")]
[IterationTime(300)]
public class DelegateRequestOnlyBenchmarks
{
    private const int RequestCount = 256;
    private const int BatchSize = 32;
    private const int MaxConcurrency = 8;
    private const int MaxPendingRequests = 512;

    private static readonly Func<IReadOnlyList<BenchmarkRequest>, CancellationToken, ValueTask> _staticHandler =
        ProcessAsync;
    private ServiceProvider _provider = null!;
    private IRequestBatcher<BenchmarkRequest> _batcher = null!;
    private BenchmarkRequest[] _requests = null!;
    private WaveState _wave = null!;
    private Func<BenchmarkRequest, Task> _submitSingle = null!;
    private Func<IEnumerable<BenchmarkRequest>, Task> _submitExplicit = null!;

    /// <summary>Gets or sets the simulated downstream delay for each handler batch.</summary>
    [Params(0, 20)]
    public int DownstreamDelayMilliseconds { get; set; }

    /// <summary>Gets or sets whether each wave uses one explicit list submission.</summary>
    [Params(false, true)]
    public bool ExplicitSubmission { get; set; }

    /// <summary>Builds, binds, and warms the typed singleton pipeline outside measurement.</summary>
    [GlobalSetup(Target = nameof(TypedSingleton))]
    public void SetupTypedSingleton() => Setup(useDelegate: false);

    /// <summary>Builds, binds, and warms the static delegate pipeline outside measurement.</summary>
    [GlobalSetup(Target = nameof(StaticDelegate))]
    public void SetupStaticDelegate() => Setup(useDelegate: true);

    /// <summary>Measures a typed singleton request-only handler.</summary>
    [Benchmark(Baseline = true)]
    public Task TypedSingleton() => RunWaveAsync();

    /// <summary>Measures a static delegate request-only handler.</summary>
    [Benchmark]
    public Task StaticDelegate() => RunWaveAsync();

    /// <summary>Drains and disposes the application-owned provider outside measurement.</summary>
    [GlobalCleanup]
    public async Task CleanupAsync() => await _provider.DisposeAsync().ConfigureAwait(false);

    private void Setup(bool useDelegate)
    {
        _wave = new WaveState(DownstreamDelayMilliseconds);
        _requests = Enumerable.Range(0, RequestCount)
            .Select(static id => new BenchmarkRequest(id))
            .ToArray();
        foreach (var request in _requests)
        {
            request.State = _wave;
        }

        var services = new ServiceCollection();
        if (useDelegate)
        {
            services.AddDelegateRequestBatcher<BenchmarkRequest>(Configure);
        }
        else
        {
            services.AddRequestBatcher<BenchmarkRequest, TypedSingletonHandler>(
                ServiceLifetime.Singleton,
                Configure);
        }

        _provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
        _batcher = _provider.GetRequiredService<IRequestBatcher<BenchmarkRequest>>();

        if (useDelegate)
        {
            _submitSingle = SubmitDelegateSingle;
            _submitExplicit = SubmitDelegateExplicit;
        }
        else
        {
            _submitSingle = SubmitTypedSingle;
            _submitExplicit = SubmitTypedExplicit;
        }

        // Bind the selected delegate and warm the pipeline before BenchmarkDotNet measures it.
        RunWaveAsync().GetAwaiter().GetResult();
    }

    private void Configure(RequestBatchOptions<BenchmarkRequest> options)
    {
        options.BatchSize = BatchSize;
        options.MaxConcurrency = MaxConcurrency;
        options.MaxPendingRequests = MaxPendingRequests;
        options.FullMode = RequestBatchFullMode.Wait;
    }

    private Task SubmitTypedSingle(BenchmarkRequest request) => _batcher.ExecuteAsync(request);

    private Task SubmitTypedExplicit(IEnumerable<BenchmarkRequest> requests) => _batcher.ExecuteAsync(requests);

    private Task SubmitDelegateSingle(BenchmarkRequest request) => _batcher.ExecuteAsync(request, _staticHandler);

    private Task SubmitDelegateExplicit(IEnumerable<BenchmarkRequest> requests) =>
        _batcher.ExecuteAsync(requests, _staticHandler);

    private async Task RunWaveAsync()
    {
        _wave.Begin();
        if (ExplicitSubmission)
        {
            await _submitExplicit(_requests).ConfigureAwait(false);
        }
        else
        {
            var submissions = new Task[RequestCount];
            for (var index = 0; index < submissions.Length; index++)
            {
                submissions[index] = _submitSingle(_requests[index]);
            }

            await Task.WhenAll(submissions).ConfigureAwait(false);
        }

        _wave.Validate();
    }

    private static async ValueTask ProcessAsync(
        IReadOnlyList<BenchmarkRequest> requests,
        CancellationToken cancellationToken)
    {
        var delay = requests[0].State.DownstreamDelayMilliseconds;
        if (delay == 0)
        {
            await Task.Yield();
        }
        else
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }

        foreach (var request in requests)
        {
            request.State.Record(request.Id, Transform(request.Id));
        }
    }

    private static int Transform(int id) => (id * 31) + 7;

    private sealed class TypedSingletonHandler : IRequestBatchHandler<BenchmarkRequest>
    {
        public ValueTask HandleAsync(
            IReadOnlyList<BenchmarkRequest> requests,
            CancellationToken cancellationToken = default) => ProcessAsync(requests, cancellationToken);
    }

    private sealed class BenchmarkRequest(int id)
    {
        public int Id { get; } = id;

        public WaveState State { get; set; } = null!;
    }

    private sealed class WaveState(int downstreamDelayMilliseconds)
    {
        private readonly int[] _processed = new int[RequestCount];
        private readonly int[] _results = new int[RequestCount];

        public int DownstreamDelayMilliseconds { get; } = downstreamDelayMilliseconds;

        public void Begin()
        {
            Array.Clear(_processed);
            Array.Clear(_results);
        }

        public void Record(int id, int result)
        {
            Volatile.Write(ref _results[id], result);
            Interlocked.Increment(ref _processed[id]);
        }

        public void Validate()
        {
            for (var id = 0; id < RequestCount; id++)
            {
                var processed = Volatile.Read(ref _processed[id]);
                var result = Volatile.Read(ref _results[id]);
                if (processed != 1 || result != Transform(id))
                {
                    throw new InvalidOperationException(
                        $"Request {id} was processed {processed} times with result {result}; expected exactly once.");
                }
            }
        }
    }
}
