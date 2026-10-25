using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using Microsoft.Extensions.DependencyInjection;
using RequestBatcher.DelegateHandlers;

namespace RequestBatcher.Benchmarks.InMemory;

/// <summary>
/// Measures the per-batch cost of creating and disposing the scope used by a delegate
/// that injects a singleton service.
/// </summary>
/// <remarks>
/// The benchmark isolates dependency-resolution and scope overhead with synchronous work.
/// It is not a full pipeline or downstream-latency comparison. Provider construction,
/// singleton creation, delegate binding and warm-up are outside the measured methods.
/// </remarks>
[MemoryDiagnoser]
[SimpleJob(
    RunStrategy.Throughput,
    launchCount: 1,
    warmupCount: 5,
    iterationCount: 12,
    id: "BatchScope")]
[IterationTime(300)]
public class BatchScopeBenchmarks
{
    private static readonly Action<IReadOnlyList<ScopeRequest>, ScopeDependency> _handler = Process;

    private ServiceProvider _provider = null!;
    private IServiceScopeFactory _scopeFactory = null!;
    private ScopeDependency _singleton = null!;
    private DelegateRequestBatchHandler<ScopeRequest> _delegateHandler = null!;
    private ScopeRequest[] _requests = null!;

    /// <summary>Builds the provider and warms every measured path outside the benchmark.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _provider = new ServiceCollection()
            .AddSingleton<ScopeDependency>()
            .BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true,
            });
        _singleton = _provider.GetRequiredService<ScopeDependency>();
        _requests = [new ScopeRequest(41, _singleton)];

        _scopeFactory = _provider.GetRequiredService<IServiceScopeFactory>();
        _delegateHandler = new DelegateRequestBatchHandler<ScopeRequest>(_scopeFactory);
        _delegateHandler.Bind(_handler);

        RootSingletonResolution();
        EmptyBatchScope().GetAwaiter().GetResult();
        BatchScopeSingletonResolution().GetAwaiter().GetResult();
        DelegateBatchScopeSingletonResolution().GetAwaiter().GetResult();
        EnsureSingletonUsable();
    }

    /// <summary>Disposes the root provider and verifies that the singleton was disposed once.</summary>
    [GlobalCleanup]
    public async Task CleanupAsync()
    {
        var prematureDisposalCount = _singleton.DisposeCount;
        await _provider.DisposeAsync().ConfigureAwait(false);
        if (prematureDisposalCount != 0 || _singleton.DisposeCount != 1)
        {
            throw new InvalidOperationException(
                $"The singleton was disposed {_singleton.DisposeCount} times; expected exactly once.");
        }
    }

    /// <summary>Measures resolving the singleton from the root provider without a batch scope.</summary>
    [Benchmark(Baseline = true)]
    public void RootSingletonResolution()
    {
        ResetRequest();
        var dependency = _provider.GetRequiredService<ScopeDependency>();
        Process(_requests, dependency);
        ValidateRequest();
    }

    /// <summary>Measures creating and disposing an otherwise unused async batch scope.</summary>
    [Benchmark]
    public async ValueTask EmptyBatchScope()
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        if (ReferenceEquals(scope.ServiceProvider, _provider))
        {
            throw new InvalidOperationException("A batch scope must have its own service provider.");
        }
    }

    /// <summary>Measures a batch scope plus resolving the singleton and invoking the handler.</summary>
    [Benchmark]
    public async ValueTask BatchScopeSingletonResolution()
    {
        ResetRequest();
        await using var scope = _scopeFactory.CreateAsyncScope();
        var dependency = scope.ServiceProvider.GetRequiredService<ScopeDependency>();
        Process(_requests, dependency);
        ValidateRequest();
    }

    /// <summary>Measures the actual delegate forwarding path, including its batch scope.</summary>
    [Benchmark]
    public ValueTask DelegateBatchScopeSingletonResolution()
    {
        ResetRequest();
        var pending = _delegateHandler.HandleAsync(_requests);
        if (pending.IsCompletedSuccessfully)
        {
            pending.GetAwaiter().GetResult();
            ValidateRequest();
            return default;
        }

        return AwaitAndValidateAsync(pending, this);
    }

    private static async ValueTask AwaitAndValidateAsync(
        ValueTask pending,
        BatchScopeBenchmarks benchmark)
    {
        await pending.ConfigureAwait(false);
        benchmark.ValidateRequest();
    }

    private static void Process(
        IReadOnlyList<ScopeRequest> requests,
        ScopeDependency dependency)
    {
        // Index the interface to avoid measuring an array enumerator allocation as scope overhead.
        for (var index = 0; index < requests.Count; index++)
        {
            var request = requests[index];
            if (!ReferenceEquals(dependency, request.ExpectedDependency) || dependency.IsDisposed)
            {
                throw new InvalidOperationException("The batch did not resolve the live root singleton.");
            }

            if (dependency.Transform(request.Value) != request.Value + 1)
            {
                throw new InvalidOperationException("The singleton dependency returned an invalid value.");
            }

            Interlocked.Increment(ref request.Processed);
        }
    }

    private void ResetRequest()
    {
        _requests[0].Processed = 0;
        EnsureSingletonUsable();
    }

    private void EnsureSingletonUsable()
    {
        if (_singleton.DisposeCount != 0)
        {
            throw new InvalidOperationException("A batch scope disposed the root singleton.");
        }
    }

    private void ValidateRequest()
    {
        if (Volatile.Read(ref _requests[0].Processed) != 1)
        {
            throw new InvalidOperationException(
                $"The request was processed {Volatile.Read(ref _requests[0].Processed)} times; expected exactly once.");
        }
    }

    private sealed class ScopeRequest(int value, ScopeDependency expectedDependency)
    {
        public int Value { get; } = value;

        public ScopeDependency ExpectedDependency { get; } = expectedDependency;

        public int Processed;
    }

    private sealed class ScopeDependency : IAsyncDisposable
    {
        private int _disposed;
        private int _disposeCount;

        public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public int Transform(int value) => value + 1;

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCount);
            Volatile.Write(ref _disposed, 1);
            return default;
        }
    }
}
