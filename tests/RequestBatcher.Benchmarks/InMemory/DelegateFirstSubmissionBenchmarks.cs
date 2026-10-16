using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Engines;
using BenchmarkDotNet.Jobs;
using Microsoft.Extensions.DependencyInjection;

namespace RequestBatcher.Benchmarks.InMemory;

/// <summary>
/// Measures the first response submission through a freshly constructed typed or delegate pipeline.
/// </summary>
[MemoryDiagnoser]
[Config(typeof(FirstSubmissionConfig))]
public class DelegateFirstSubmissionBenchmarks
{
    private const string BatcherName = "first-submission";
    private const int RequestValue = 42;

    private static int _handledCount;
    private ServiceProvider? _provider;
    private IRequestBatcher<int, int>? _batcher;

    /// <summary>
    /// Gets or sets whether the handler receives the equivalent scoped dependency.
    /// </summary>
    [Params(false, true)]
    public bool InjectService { get; set; }

    /// <summary>
    /// Gets or sets whether the pipeline is registered and resolved by name.
    /// </summary>
    [Params(false, true)]
    public bool Named { get; set; }

    /// <summary>
    /// Creates a fresh typed pipeline before each timed iteration.
    /// </summary>
    [IterationSetup(Target = nameof(TypedFirstSubmission))]
    public void SetupTyped() => Setup(useDelegate: false);

    /// <summary>
    /// Creates a fresh delegate pipeline before each timed iteration.
    /// </summary>
    [IterationSetup(Target = nameof(DelegateFirstSubmission))]
    public void SetupDelegate() => Setup(useDelegate: true);

    /// <summary>
    /// Measures the first typed response submission for a fresh pipeline.
    /// </summary>
    [Benchmark(Baseline = true)]
    public async Task<int> TypedFirstSubmission()
    {
        var response = await Batcher.ExecuteAsync(RequestValue).ConfigureAwait(false);
        Validate(response);
        return response;
    }

    /// <summary>
    /// Measures the first static delegate response submission for a fresh pipeline.
    /// </summary>
    [Benchmark]
    public async Task<int> DelegateFirstSubmission()
    {
        var response = InjectService
            ? await Batcher.ExecuteAsync(
                RequestValue,
                static (IReadOnlyList<RequestBatchEntry<int, int>> entries, ScopedDependency dependency) =>
                    CompleteAsync(entries, dependency)).ConfigureAwait(false)
            : await Batcher.ExecuteAsync(
                RequestValue,
                static (IReadOnlyList<RequestBatchEntry<int, int>> entries) => CompleteAsync(entries, null))
                .ConfigureAwait(false);

        Validate(response);
        return response;
    }

    /// <summary>
    /// Disposes the fresh provider after each timed iteration.
    /// </summary>
    [IterationCleanup(Targets = new[] { nameof(TypedFirstSubmission), nameof(DelegateFirstSubmission) })]
    public void CleanupIteration()
    {
        if (_provider is null)
        {
            return;
        }

        _provider.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _provider = null;
        _batcher = null;
    }

    private IRequestBatcher<int, int> Batcher =>
        _batcher ?? throw new InvalidOperationException("The benchmark has not been initialized.");

    private void Setup(bool useDelegate)
    {
        Volatile.Write(ref _handledCount, 0);

        var services = new ServiceCollection();
        if (InjectService)
        {
            services.AddScoped<ScopedDependency>();
        }

        if (useDelegate)
        {
            if (Named)
            {
                services.AddDelegateRequestBatcher<int, int>(BatcherName, ConfigureOptions);
            }
            else
            {
                services.AddDelegateRequestBatcher<int, int>(ConfigureOptions);
            }
        }
        else if (InjectService)
        {
            if (Named)
            {
                services.AddRequestBatcher<int, int, ScopedTypedHandler>(
                    BatcherName,
                    ServiceLifetime.Scoped,
                    ConfigureOptions);
            }
            else
            {
                services.AddRequestBatcher<int, int, ScopedTypedHandler>(
                    ServiceLifetime.Scoped,
                    ConfigureOptions);
            }
        }
        else if (Named)
        {
            services.AddRequestBatcher<int, int, SingletonTypedHandler>(
                BatcherName,
                ServiceLifetime.Singleton,
                ConfigureOptions);
        }
        else
        {
            services.AddRequestBatcher<int, int, SingletonTypedHandler>(
                ServiceLifetime.Singleton,
                ConfigureOptions);
        }

        _provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
        _batcher = Named
            ? _provider.GetRequiredKeyedService<IRequestBatcher<int, int>>(BatcherName)
            : _provider.GetRequiredService<IRequestBatcher<int, int>>();
    }

    private static void ConfigureOptions(RequestBatchOptions<int> options)
    {
        options.BatchSize = 1;
        options.MaxConcurrency = 1;
        options.MaxPendingRequests = 16;
        options.FullMode = RequestBatchFullMode.Wait;
    }

    private static ValueTask CompleteAsync(
        IReadOnlyList<RequestBatchEntry<int, int>> entries,
        ScopedDependency? dependency)
    {
        if (entries.Count != 1)
        {
            throw new InvalidOperationException("The first-submission benchmark must dispatch one entry.");
        }

        var entry = entries[0];
        entry.SetResponse(dependency is null ? entry.Request : dependency.Transform(entry.Request));
        Interlocked.Increment(ref _handledCount);
        return ValueTask.CompletedTask;
    }

    private static void Validate(int response)
    {
        if (response != RequestValue || Volatile.Read(ref _handledCount) != 1)
        {
            throw new InvalidOperationException(
                $"The first submission returned {response} after {_handledCount} handler executions; " +
                $"expected {RequestValue} and exactly one execution.");
        }
    }

    private sealed class FirstSubmissionConfig : ManualConfig
    {
        public FirstSubmissionConfig()
        {
            AddJob(Job.Default
                .WithStrategy(RunStrategy.ColdStart)
                .WithLaunchCount(2)
                .WithWarmupCount(0)
                .WithIterationCount(8)
                .WithInvocationCount(1)
                .WithUnrollFactor(1)
                .WithId("DelegateFirstSubmission"));
        }
    }

    private sealed class SingletonTypedHandler : IRequestBatchHandler<int, int>
    {
        public ValueTask HandleAsync(
            IReadOnlyList<RequestBatchEntry<int, int>> entries,
            CancellationToken cancellationToken = default) => CompleteAsync(entries, null);
    }

    private sealed class ScopedTypedHandler(ScopedDependency dependency) : IRequestBatchHandler<int, int>
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
}
