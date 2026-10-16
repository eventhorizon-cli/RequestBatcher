using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Engines;
using Microsoft.Extensions.DependencyInjection;
using RequestBatcher.Benchmarks.Infrastructure;

namespace RequestBatcher.Benchmarks.InMemory;

/// <summary>
/// Compares typed handlers and cached delegate handlers using identical batch work.
/// </summary>
[MemoryDiagnoser]
[CategoriesColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[SimpleJob(RunStrategy.Throughput, launchCount: 2, warmupCount: 5, iterationCount: 12, id: "DelegateHandlers")]
[IterationTime(300)]
public class DelegateHandlerBenchmarks
{
    private const int RequestCount = 256;
    private static WaveState _wave = null!;
    private readonly int[] _requests = Enumerable.Range(0, RequestCount).ToArray();
    private ServiceProvider _provider = null!;
    private IRequestBatcher<int, int> _batcher = null!;
    private Func<int, Task<int>> _submitSingle = null!;
    private Func<IEnumerable<int>, Task<IReadOnlyList<int>>> _submitBatch = null!;

    /// <summary>Gets or sets the maximum number of requests in one handler batch.</summary>
    [Params(1, 32, 256)]
    public int BatchSize { get; set; }

    /// <summary>Gets or sets the maximum number of concurrently executing handler batches.</summary>
    [Params(1, 8)]
    public int MaxConcurrency { get; set; }

    /// <summary>
    /// Builds the typed singleton control without per-batch service resolution.
    /// </summary>
    [GlobalSetup(Target = nameof(TypedBatchNoServices))]
    public void SetupTypedNoServices() => Setup(useDelegate: false, useServices: false);

    /// <summary>
    /// Builds and warms the delegate pipeline without per-batch service resolution.
    /// </summary>
    [GlobalSetup(Target = nameof(DelegateBatchNoServices))]
    public void SetupDelegateNoServices() => Setup(useDelegate: true, useServices: false);

    /// <summary>
    /// Builds the typed scoped control with a scoped dependency.
    /// </summary>
    [GlobalSetup(Target = nameof(TypedBatchScopedService))]
    public void SetupTypedScoped() => Setup(useDelegate: false, useServices: true);

    /// <summary>
    /// Builds and warms the delegate pipeline with the same scoped dependency.
    /// </summary>
    [GlobalSetup(Target = nameof(DelegateBatchScopedService))]
    public void SetupDelegateScoped() => Setup(useDelegate: true, useServices: true);

    /// <summary>Builds and warms the typed singleton single-submission control.</summary>
    [GlobalSetup(Target = nameof(TypedSingleNoServices))]
    public void SetupTypedSingleNoServices() => Setup(useDelegate: false, useServices: false, single: true);

    /// <summary>Builds and binds the delegate single-submission case without services.</summary>
    [GlobalSetup(Target = nameof(DelegateSingleNoServices))]
    public void SetupDelegateSingleNoServices() => Setup(useDelegate: true, useServices: false, single: true);

    /// <summary>Builds and warms the typed scoped single-submission control.</summary>
    [GlobalSetup(Target = nameof(TypedSingleScopedService))]
    public void SetupTypedSingleScoped() => Setup(useDelegate: false, useServices: true, single: true);

    /// <summary>Builds and binds the delegate single-submission case with a scoped service.</summary>
    [GlobalSetup(Target = nameof(DelegateSingleScopedService))]
    public void SetupDelegateSingleScoped() => Setup(useDelegate: true, useServices: true, single: true);

    /// <summary>Measures single submissions with a typed singleton handler.</summary>
    [Benchmark(Baseline = true), BenchmarkCategory("SingleNoServices")]
    public Task TypedSingleNoServices() => RunSingleAsync();

    /// <summary>Measures single submissions with a delegate and no services.</summary>
    [Benchmark, BenchmarkCategory("SingleNoServices")]
    public Task DelegateSingleNoServices() => RunSingleAsync();

    /// <summary>Measures explicit submissions with a typed singleton handler.</summary>
    [Benchmark(Baseline = true), BenchmarkCategory("BatchNoServices")]
    public Task TypedBatchNoServices() => RunBatchAsync();

    /// <summary>Measures explicit submissions with a delegate and no services.</summary>
    [Benchmark, BenchmarkCategory("BatchNoServices")]
    public Task DelegateBatchNoServices() => RunBatchAsync();

    /// <summary>Measures single submissions with a typed scoped handler.</summary>
    [Benchmark(Baseline = true), BenchmarkCategory("SingleScopedService")]
    public Task TypedSingleScopedService() => RunSingleAsync();

    /// <summary>Measures single submissions with a delegate and a scoped dependency.</summary>
    [Benchmark, BenchmarkCategory("SingleScopedService")]
    public Task DelegateSingleScopedService() => RunSingleAsync();

    /// <summary>Measures explicit submissions with a typed scoped handler.</summary>
    [Benchmark(Baseline = true), BenchmarkCategory("BatchScopedService")]
    public Task TypedBatchScopedService() => RunBatchAsync();

    /// <summary>Measures explicit submissions with a delegate and a scoped dependency.</summary>
    [Benchmark, BenchmarkCategory("BatchScopedService")]
    public Task DelegateBatchScopedService() => RunBatchAsync();

    /// <summary>Drains and disposes the provider outside measurement.</summary>
    [GlobalCleanup]
    public async Task CleanupAsync() => await _provider.DisposeAsync().ConfigureAwait(false);

    private void Setup(bool useDelegate, bool useServices, bool single = false)
    {
        _wave = new WaveState();
        var services = new ServiceCollection();
        services.AddScoped<ScopedDependency>();
        if (useDelegate)
        {
            services.AddDelegateRequestBatcher<int, int>(Configure);
        }
        else if (useServices)
        {
            services.AddRequestBatcher<int, int, ScopedHandler>(ServiceLifetime.Scoped, Configure);
        }
        else
        {
            services.AddRequestBatcher<int, int, SingletonHandler>(ServiceLifetime.Singleton, Configure);
        }

        _provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
        _batcher = _provider.GetRequiredService<IRequestBatcher<int, int>>();
        if (!useDelegate)
        {
            _submitSingle = request => _batcher.ExecuteAsync(request);
            _submitBatch = requests => _batcher.ExecuteAsync(requests);
        }
        else if (useServices)
        {
            _submitSingle = SubmitSingleWithService;
            _submitBatch = SubmitBatchWithService;
        }
        else
        {
            _submitSingle = SubmitSingleWithoutService;
            _submitBatch = SubmitBatchWithoutService;
        }

        // Bind and compile the selected static lambda before measurement.
        (single ? RunSingleAsync() : RunBatchAsync()).GetAwaiter().GetResult();
    }

    private void Configure(RequestBatchOptions<int> options)
    {
        options.BatchSize = BatchSize;
        options.MaxConcurrency = MaxConcurrency;
        options.MaxPendingRequests = 512;
        options.FullMode = RequestBatchFullMode.Wait;
    }

    private Task<int> SubmitSingleWithoutService(int request) => _batcher.ExecuteAsync(
        request,
        static (IReadOnlyList<RequestBatchEntry<int, int>> entries) => CompleteAsync(entries, null));

    private Task<IReadOnlyList<int>> SubmitBatchWithoutService(IEnumerable<int> requests) => _batcher.ExecuteAsync(
        requests,
        static (IReadOnlyList<RequestBatchEntry<int, int>> entries) => CompleteAsync(entries, null));

    private Task<int> SubmitSingleWithService(int request) => _batcher.ExecuteAsync(
        request,
        static (IReadOnlyList<RequestBatchEntry<int, int>> entries, ScopedDependency dependency) =>
            CompleteAsync(entries, dependency));

    private Task<IReadOnlyList<int>> SubmitBatchWithService(IEnumerable<int> requests) => _batcher.ExecuteAsync(
        requests,
        static (IReadOnlyList<RequestBatchEntry<int, int>> entries, ScopedDependency dependency) =>
            CompleteAsync(entries, dependency));

    private async Task RunSingleAsync()
    {
        _wave.Begin();
        var tasks = new Task<int>[RequestCount];
        Task<int[]> all;
        try
        {
            for (var index = 0; index < tasks.Length; index++)
            {
                tasks[index] = _submitSingle(_requests[index]);
            }

            all = Task.WhenAll(tasks);
        }
        finally
        {
            _wave.Gate.Release();
        }

        _wave.Validate(await all.ConfigureAwait(false));
    }

    private async Task RunBatchAsync()
    {
        _wave.Begin();
        Task<IReadOnlyList<int>> all;
        try
        {
            all = _submitBatch(_requests);
        }
        finally
        {
            _wave.Gate.Release();
        }

        _wave.Validate(await all.ConfigureAwait(false));
    }

    private static async ValueTask CompleteAsync(
        IReadOnlyList<RequestBatchEntry<int, int>> entries,
        ScopedDependency? dependency)
    {
        await _wave.Gate.WaitAsync().ConfigureAwait(false);
        foreach (var entry in entries)
        {
            Interlocked.Increment(ref _wave.Processed[entry.Request]);
            entry.SetResponse(dependency is null ? entry.Request : dependency.Transform(entry.Request));
        }
    }

    private sealed class SingletonHandler : IRequestBatchHandler<int, int>
    {
        public ValueTask HandleAsync(
            IReadOnlyList<RequestBatchEntry<int, int>> entries,
            CancellationToken cancellationToken = default) => CompleteAsync(entries, null);
    }

    private sealed class ScopedHandler(ScopedDependency dependency) : IRequestBatchHandler<int, int>
    {
        public ValueTask HandleAsync(
            IReadOnlyList<RequestBatchEntry<int, int>> entries,
            CancellationToken cancellationToken = default) => CompleteAsync(entries, dependency);
    }

    private sealed class ScopedDependency : IAsyncDisposable
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

        public void Validate(IReadOnlyList<int> responses)
        {
            if (responses.Count != RequestCount)
            {
                throw new InvalidOperationException("The benchmark returned an incorrect response count.");
            }

            for (var index = 0; index < RequestCount; index++)
            {
                if (responses[index] != index || Volatile.Read(ref Processed[index]) != 1)
                {
                    throw new InvalidOperationException($"Request {index} did not complete exactly once with its result.");
                }
            }
        }
    }
}
