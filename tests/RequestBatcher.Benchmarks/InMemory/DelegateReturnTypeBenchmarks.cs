using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using Microsoft.Extensions.DependencyInjection;

namespace RequestBatcher.Benchmarks.InMemory;

/// <summary>
/// Compares supported delegate return types when identical response work completes synchronously.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Throughput, launchCount: 2, warmupCount: 5, iterationCount: 12, id: "DelegateReturnTypes")]
[IterationTime(300)]
public class DelegateReturnTypeBenchmarks
{
    private const int RequestCount = 256;
    private static readonly int[] _processed = new int[RequestCount];
    private readonly int[] _requests = Enumerable.Range(0, RequestCount).ToArray();
    private ServiceProvider _provider = null!;
    private IRequestBatcher<int, int> _batcher = null!;
    private Func<Task<IReadOnlyList<int>>> _submit = null!;

    /// <summary>Gets or sets the maximum size of a handler batch.</summary>
    [Params(1, 32, 256)]
    public int BatchSize { get; set; }

    /// <summary>Creates and warms the typed singleton control.</summary>
    [GlobalSetup(Target = nameof(Typed))]
    public void SetupTyped() => Setup(useDelegate: false, SubmitTyped);

    /// <summary>Creates and binds the synchronous void delegate.</summary>
    [GlobalSetup(Target = nameof(DelegateVoid))]
    public void SetupVoid() => Setup(useDelegate: true, SubmitVoid);

    /// <summary>Creates and binds the completed-task delegate.</summary>
    [GlobalSetup(Target = nameof(DelegateTask))]
    public void SetupTask() => Setup(useDelegate: true, SubmitTask);

    /// <summary>Creates and binds the completed-value-task delegate.</summary>
    [GlobalSetup(Target = nameof(DelegateValueTask))]
    public void SetupValueTask() => Setup(useDelegate: true, SubmitValueTask);

    /// <summary>Measures synchronous work in a typed singleton handler.</summary>
    [Benchmark(Baseline = true)]
    public Task Typed() => RunAsync();

    /// <summary>Measures synchronous work in a void delegate.</summary>
    [Benchmark]
    public Task DelegateVoid() => RunAsync();

    /// <summary>Measures synchronous work in a delegate returning an already completed task.</summary>
    [Benchmark]
    public Task DelegateTask() => RunAsync();

    /// <summary>Measures synchronous work in a delegate returning a completed value task.</summary>
    [Benchmark]
    public Task DelegateValueTask() => RunAsync();

    /// <summary>Disposes the batcher outside measurement.</summary>
    [GlobalCleanup]
    public async Task CleanupAsync() => await _provider.DisposeAsync().ConfigureAwait(false);

    private void Setup(bool useDelegate, Func<Task<IReadOnlyList<int>>> submit)
    {
        var services = new ServiceCollection();
        if (useDelegate)
        {
            services.AddDelegateRequestBatcher<int, int>(Configure);
        }
        else
        {
            services.AddRequestBatcher<int, int, Handler>(ServiceLifetime.Singleton, Configure);
        }

        _provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
        _batcher = _provider.GetRequiredService<IRequestBatcher<int, int>>();
        _submit = submit;
        RunAsync().GetAwaiter().GetResult();
    }

    private void Configure(RequestBatchOptions<int> options)
    {
        options.BatchSize = BatchSize;
        options.MaxConcurrency = 8;
        options.MaxPendingRequests = 512;
        options.FullMode = RequestBatchFullMode.Wait;
    }

    private Task<IReadOnlyList<int>> SubmitTyped() => _batcher.ExecuteAsync(_requests);

    private Task<IReadOnlyList<int>> SubmitVoid() => _batcher.ExecuteAsync(
        _requests,
        static (IReadOnlyList<RequestBatchEntry<int, int>> entries) => Complete(entries));

    private Task<IReadOnlyList<int>> SubmitTask() => _batcher.ExecuteAsync(
        _requests,
        static (IReadOnlyList<RequestBatchEntry<int, int>> entries) =>
        {
            Complete(entries);
            return Task.CompletedTask;
        });

    private Task<IReadOnlyList<int>> SubmitValueTask() => _batcher.ExecuteAsync(
        _requests,
        static (IReadOnlyList<RequestBatchEntry<int, int>> entries) =>
        {
            Complete(entries);
            return ValueTask.CompletedTask;
        });

    private async Task RunAsync()
    {
        Array.Clear(_processed);
        var responses = await _submit().ConfigureAwait(false);
        if (responses.Count != RequestCount)
        {
            throw new InvalidOperationException("The benchmark returned an incorrect response count.");
        }

        for (var index = 0; index < RequestCount; index++)
        {
            if (responses[index] != index || Volatile.Read(ref _processed[index]) != 1)
            {
                throw new InvalidOperationException($"Request {index} did not complete exactly once with its result.");
            }
        }
    }

    private static void Complete(IReadOnlyList<RequestBatchEntry<int, int>> entries)
    {
        foreach (var entry in entries)
        {
            Interlocked.Increment(ref _processed[entry.Request]);
            entry.SetResponse(entry.Request);
        }
    }

    private sealed class Handler : IRequestBatchHandler<int, int>
    {
        public ValueTask HandleAsync(
            IReadOnlyList<RequestBatchEntry<int, int>> entries,
            CancellationToken cancellationToken = default)
        {
            Complete(entries);
            return ValueTask.CompletedTask;
        }
    }
}
