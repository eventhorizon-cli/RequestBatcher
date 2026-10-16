using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using Microsoft.Extensions.DependencyInjection;
using RequestBatcher.Benchmarks.Infrastructure;

namespace RequestBatcher.Benchmarks.InMemory;

/// <summary>
/// Compares request-only typed and delegate batch submission with equivalent service injection.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(
    RunStrategy.Throughput,
    launchCount: 2,
    warmupCount: 5,
    iterationCount: 12,
    id: "DelegateServiceInjection")]
[IterationTime(300)]
public class DelegateServiceInjectionBenchmarks
{
    private const string DependencyKey = "work";
    private const int RequestCount = 256;
    private const int BatchSize = 32;
    private const int MaxConcurrency = 8;
    private const int MaxPendingRequests = 512;

    private static WaveState _wave = null!;
    private readonly int[] _requests = Enumerable.Range(0, RequestCount).ToArray();
    private ServiceProvider _provider = null!;
    private IRequestBatcher<int> _batcher = null!;
    private Func<IEnumerable<int>, Task> _submitBatch = null!;

    /// <summary>Gets or sets the lifetime of the dependency injected into each handler batch.</summary>
    [Params(ServiceLifetime.Singleton, ServiceLifetime.Scoped, ServiceLifetime.Transient)]
    public ServiceLifetime DependencyLifetime { get; set; }

    /// <summary>Gets or sets whether the dependency is selected by the explicit service key.</summary>
    [Params(false, true)]
    public bool KeyedService { get; set; }

    /// <summary>
    /// Builds the typed scoped-handler pipeline and warms its first submission outside measurement.
    /// </summary>
    [GlobalSetup(Target = nameof(TypedBatchSubmission))]
    public void SetupTyped() => Setup(useDelegate: false);

    /// <summary>
    /// Builds the singleton delegate-forwarding pipeline and binds its selected static lambda outside measurement.
    /// The forwarding handler resolves the dependency in an async scope owned by each dispatched batch.
    /// </summary>
    [GlobalSetup(Target = nameof(DelegateBatchSubmission))]
    public void SetupDelegate() => Setup(useDelegate: true);

    /// <summary>Measures explicit request batch submission through a typed scoped handler.</summary>
    [Benchmark(Baseline = true)]
    public Task TypedBatchSubmission() => RunSubmissionAsync();

    /// <summary>Measures explicit request batch submission through a bound static delegate.</summary>
    [Benchmark]
    public Task DelegateBatchSubmission() => RunSubmissionAsync();

    /// <summary>Disposes the application-owned provider after the benchmark completes.</summary>
    [GlobalCleanup]
    public async Task CleanupAsync() => await _provider.DisposeAsync().ConfigureAwait(false);

    private void Setup(bool useDelegate)
    {
        _wave = new WaveState();
        var services = new ServiceCollection();
        AddDependency(services);

        if (useDelegate)
        {
            services.AddDelegateRequestBatcher<int>(Configure);
        }
        else if (KeyedService)
        {
            services.AddRequestBatcher<int, KeyedTypedHandler>(ServiceLifetime.Scoped, Configure);
        }
        else
        {
            services.AddRequestBatcher<int, TypedHandler>(ServiceLifetime.Scoped, Configure);
        }

        _provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
        _batcher = _provider.GetRequiredService<IRequestBatcher<int>>();

        if (useDelegate)
        {
            // The selected call site remains fixed for this benchmark instance after binding.
            _submitBatch = KeyedService ? SubmitKeyedDelegateBatch : SubmitDelegateBatch;
        }
        else
        {
            _submitBatch = SubmitTypedBatch;
        }

        // Warm the selected delegate once so signature binding and expression compilation are outside measurement.
        RunSubmissionAsync().GetAwaiter().GetResult();
    }

    private void AddDependency(IServiceCollection services)
    {
        var descriptor = KeyedService
            ? new ServiceDescriptor(
                typeof(WorkDependency),
                DependencyKey,
                typeof(WorkDependency),
                DependencyLifetime)
            : new ServiceDescriptor(typeof(WorkDependency), typeof(WorkDependency), DependencyLifetime);
        services.Add(descriptor);
    }

    private static void Configure(RequestBatchOptions<int> options)
    {
        options.BatchSize = BatchSize;
        options.MaxConcurrency = MaxConcurrency;
        options.MaxPendingRequests = MaxPendingRequests;
        options.FullMode = RequestBatchFullMode.Wait;
    }

    private Task SubmitTypedBatch(IEnumerable<int> requests) => _batcher.ExecuteAsync(requests);

    private Task SubmitDelegateBatch(IEnumerable<int> requests) => _batcher.ExecuteAsync(
        requests,
        static (IReadOnlyList<int> requests, WorkDependency dependency) => ProcessAsync(requests, dependency));

    private Task SubmitKeyedDelegateBatch(IEnumerable<int> requests) => _batcher.ExecuteAsync(
        requests,
        static (IReadOnlyList<int> requests, [FromKeyedServices(DependencyKey)] WorkDependency dependency) =>
            ProcessAsync(requests, dependency));

    private static async ValueTask ProcessAsync(
        IReadOnlyList<int> requests,
        WorkDependency dependency)
    {
        await _wave.Gate.WaitAsync().ConfigureAwait(false);
        foreach (var request in requests)
        {
            Interlocked.Increment(ref _wave.Processed[request]);
            if (dependency.Transform(request) != request)
            {
                throw new InvalidOperationException("The dependency changed the request value.");
            }
        }
    }

    private async Task RunSubmissionAsync()
    {
        _wave.Begin();
        Task completion;
        try
        {
            completion = _submitBatch(_requests);
        }
        finally
        {
            _wave.Gate.Release();
        }

        await completion.ConfigureAwait(false);
        _wave.Validate();
    }

    private sealed class TypedHandler(WorkDependency dependency) : IRequestBatchHandler<int>
    {
        public ValueTask HandleAsync(
            IReadOnlyList<int> requests,
            CancellationToken cancellationToken = default) => ProcessAsync(requests, dependency);
    }

    private sealed class KeyedTypedHandler(
        [FromKeyedServices(DependencyKey)] WorkDependency dependency) : IRequestBatchHandler<int>
    {
        public ValueTask HandleAsync(
            IReadOnlyList<int> requests,
            CancellationToken cancellationToken = default) => ProcessAsync(requests, dependency);
    }

    private sealed class WorkDependency : IAsyncDisposable
    {
        public int Transform(int value) => value;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class WaveState
    {
        public BatchStartGate Gate { get; } = new();

        public int[] Processed { get; } = new int[RequestCount];

        public void Begin()
        {
            Array.Clear(Processed);
            Gate.Reset();
        }

        public void Validate()
        {
            for (var request = 0; request < RequestCount; request++)
            {
                if (Volatile.Read(ref Processed[request]) != 1)
                {
                    throw new InvalidOperationException(
                        $"Request {request} was processed {Volatile.Read(ref Processed[request])} times; expected exactly once.");
                }
            }
        }
    }
}
