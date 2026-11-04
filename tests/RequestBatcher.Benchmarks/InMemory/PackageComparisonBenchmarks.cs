using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;

namespace RequestBatcher.Benchmarks.InMemory;

/// <summary>
/// Measures response-bearing RequestBatcher submissions using only its public API.
/// </summary>
/// <remarks>
/// Each wave contains 256 distinct requests, uses the public response factory helper, and validates every result and
/// handler invocation. A zero-millisecond handler is the synchronous baseline; the 20-millisecond case models one
/// asynchronous downstream operation per handler batch. The delay holds a dispatch slot while producers can continue
/// filling the queue, so the delayed case exposes how downstream waiting affects batch formation. The
/// BenchmarkDotNet runner owns the job configuration so this workload can be compiled against released packages.
/// </remarks>
[MemoryDiagnoser]
public class PackageComparisonBenchmarks
{
    private const int RequestCount = 256;
    // BatchSize is the maximum handler input size; explicit submissions can still be split into multiple handler calls.
    private const int BatchSize = 32;
    // MaxConcurrency is a dispatch bound. The validation confirms it is not exceeded, but does not prove steady-state throughput.
    private const int MaxConcurrency = 8;
    private const int SingleRequestCount = RequestCount / 2;
    private const int ExplicitGroupSize = 16;

    private static readonly Func<
        IReadOnlyList<RequestBatchEntry<int, int>>,
        CancellationToken,
        ValueTask> _delegateHandler = HandleDelegateAsync;
    private static readonly WaveProbe _probe = new();
    private static readonly int[] _requests = Enumerable.Range(0, RequestCount).ToArray();

    private readonly Task<int>[] _singleTasks = new Task<int>[RequestCount];
    private readonly Task<IReadOnlyList<int>>[] _mixedGroupTasks =
        new Task<IReadOnlyList<int>>[RequestCount / (2 * ExplicitGroupSize)];
    private readonly Task[] _mixedAwaitTasks = new Task[SingleRequestCount + RequestCount / (2 * ExplicitGroupSize)];
    private readonly int[][] _mixedGroups = CreateMixedGroups();
    private ServiceProvider? _serviceProvider;
    private IRequestBatcher<int, int>? _batcher;
    private long _completedWaves;
    private long _handlerCalls;
    private string? _method;

    /// <summary>
    /// Gets or sets whether requests use the cached static delegate handler.
    /// </summary>
    [Params(false, true)]
    public bool UseDelegate { get; set; }

    /// <summary>
    /// Gets or sets the simulated asynchronous downstream delay applied once per handler batch.
    /// </summary>
    [Params(0, 20)]
    public int DownstreamDelayMilliseconds { get; set; }

    /// <summary>
    /// Builds and primes the same public submission pipeline outside measurement.
    /// </summary>
    [GlobalSetup]
    public void GlobalSetup()
    {
        _probe.Configure(DownstreamDelayMilliseconds);
        var services = new ServiceCollection();
        if (UseDelegate)
        {
            services.AddDelegateRequestBatcher<int, int>(ConfigureOptions);
        }
        else
        {
            services.AddSingleton(_probe);
            services.AddRequestBatcher<int, int, TypedResponseHandler>(
                ServiceLifetime.Singleton,
                ConfigureOptions);
        }

        _serviceProvider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
        _batcher = _serviceProvider.GetRequiredService<IRequestBatcher<int, int>>();

        RunSingleResponsesAsync().GetAwaiter().GetResult();
        RunExplicitResponsesAsync().GetAwaiter().GetResult();
        RunMixedResponsesAsync().GetAwaiter().GetResult();
        _completedWaves = 0;
        _handlerCalls = 0;
    }

    /// <summary>
    /// Measures one complete 256-request wave of single response submissions.
    /// </summary>
    [Benchmark(Description = "Single responses")]
    public Task SingleResponses() => RunSingleResponsesAsync();

    /// <summary>
    /// Measures one complete wave from an explicit submission containing all 256 requests.
    /// </summary>
    [Benchmark(Description = "Explicit responses")]
    public Task ExplicitResponses() => RunExplicitResponsesAsync();

    /// <summary>
    /// Measures one complete wave from 128 single submissions and eight explicit groups of 16 requests.
    /// </summary>
    [Benchmark(Description = "Mixed responses")]
    public Task MixedResponses() => RunMixedResponsesAsync();

    /// <summary>
    /// Drains the provider and writes one successful validation record.
    /// </summary>
    [GlobalCleanup]
    public async Task GlobalCleanupAsync()
    {
        if (_serviceProvider is not null)
        {
            await _serviceProvider.DisposeAsync().ConfigureAwait(false);
            _serviceProvider = null;
        }

        if (_completedWaves > 0)
        {
            Console.WriteLine(
                $"Package comparison validation: Method={_method}, " +
                $"Framework={System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}, " +
                $"UseDelegate={UseDelegate}, DownstreamDelayMilliseconds={DownstreamDelayMilliseconds}, " +
                $"RequestsPerWave={RequestCount}, Waves={_completedWaves}, " +
                $"HandlerCalls={_handlerCalls}, ExactlyOnce=True, ResponseOrder=True, DispatchBounds=True");
        }
    }

    private IRequestBatcher<int, int> Batcher =>
        _batcher ?? throw new InvalidOperationException("The benchmark has not been initialized.");

    private static void ConfigureOptions(RequestBatchOptions<int> options)
    {
        options.BatchSize = BatchSize;
        options.MaxConcurrency = MaxConcurrency;
        options.MaxPendingRequests = RequestCount;
        options.FullMode = RequestBatchFullMode.Wait;
    }

    private static ValueTask HandleDelegateAsync(
        IReadOnlyList<RequestBatchEntry<int, int>> entries,
        CancellationToken cancellationToken) =>
        _probe.HandleAsync(entries, cancellationToken);

    private async Task RunSingleResponsesAsync()
    {
        _method = "SingleResponses";
        _probe.BeginWave();
        for (var requestIndex = 0; requestIndex < RequestCount; requestIndex++)
        {
            _singleTasks[requestIndex] = UseDelegate
                ? Batcher.ExecuteAsync(_requests[requestIndex], _delegateHandler)
                : Batcher.ExecuteAsync(_requests[requestIndex]);
        }

        var responses = await Task.WhenAll(_singleTasks).ConfigureAwait(false);
        for (var requestIndex = 0; requestIndex < RequestCount; requestIndex++)
        {
            ValidateResponse(requestIndex, responses[requestIndex]);
        }

        _handlerCalls += _probe.ValidateWave();
        _completedWaves++;
    }

    private async Task RunExplicitResponsesAsync()
    {
        _method = "ExplicitResponses";
        _probe.BeginWave();
        var submission = UseDelegate
            ? Batcher.ExecuteAsync(_requests, _delegateHandler)
            : Batcher.ExecuteAsync(_requests);
        var responses = await submission.ConfigureAwait(false);
        if (responses.Count != RequestCount)
        {
            throw new InvalidOperationException(
                $"Received {responses.Count} responses; expected {RequestCount}.");
        }

        for (var requestIndex = 0; requestIndex < RequestCount; requestIndex++)
        {
            ValidateResponse(requestIndex, responses[requestIndex]);
        }

        _handlerCalls += _probe.ValidateWave();
        _completedWaves++;
    }

    private async Task RunMixedResponsesAsync()
    {
        _method = "MixedResponses";
        _probe.BeginWave();
        for (var requestIndex = 0; requestIndex < SingleRequestCount; requestIndex++)
        {
            var task = UseDelegate
                ? Batcher.ExecuteAsync(_requests[requestIndex], _delegateHandler)
                : Batcher.ExecuteAsync(_requests[requestIndex]);
            _mixedAwaitTasks[requestIndex] = _singleTasks[requestIndex] = task;
        }

        var explicitGroupCount = _mixedGroups.Length;
        for (var groupIndex = 0; groupIndex < explicitGroupCount; groupIndex++)
        {
            var task = UseDelegate
                ? Batcher.ExecuteAsync(_mixedGroups[groupIndex], _delegateHandler)
                : Batcher.ExecuteAsync(_mixedGroups[groupIndex]);
            _mixedGroupTasks[groupIndex] = task;
            _mixedAwaitTasks[SingleRequestCount + groupIndex] = task;
        }

        await Task.WhenAll(_mixedAwaitTasks).ConfigureAwait(false);
        for (var requestIndex = 0; requestIndex < SingleRequestCount; requestIndex++)
        {
            ValidateResponse(requestIndex, _singleTasks[requestIndex].GetAwaiter().GetResult());
        }

        for (var groupIndex = 0; groupIndex < explicitGroupCount; groupIndex++)
        {
            var requests = _mixedGroups[groupIndex];
            var responses = _mixedGroupTasks[groupIndex].GetAwaiter().GetResult();
            if (responses.Count != ExplicitGroupSize)
            {
                throw new InvalidOperationException(
                    $"Received {responses.Count} responses for group {groupIndex}; expected {ExplicitGroupSize}.");
            }

            for (var groupRequestIndex = 0; groupRequestIndex < ExplicitGroupSize; groupRequestIndex++)
            {
                ValidateResponse(requests[groupRequestIndex], responses[groupRequestIndex]);
            }
        }

        _handlerCalls += _probe.ValidateWave();
        _completedWaves++;
    }

    private static void ValidateResponse(int request, int response)
    {
        var expected = request * 17 + 3;
        if (response != expected)
        {
            throw new InvalidOperationException(
                $"Request {request} received response {response}; expected {expected}.");
        }
    }

    private static int[][] CreateMixedGroups()
    {
        var groups = new int[RequestCount / (2 * ExplicitGroupSize)][];
        for (var groupIndex = 0; groupIndex < groups.Length; groupIndex++)
        {
            var group = new int[ExplicitGroupSize];
            Array.Copy(_requests, SingleRequestCount + groupIndex * ExplicitGroupSize, group, 0, ExplicitGroupSize);
            groups[groupIndex] = group;
        }

        return groups;
    }

    private sealed class TypedResponseHandler(WaveProbe probe) : IRequestBatchHandler<int, int>
    {
        public ValueTask HandleAsync(
            IReadOnlyList<RequestBatchEntry<int, int>> entries,
            CancellationToken cancellationToken = default) =>
            probe.HandleAsync(entries, cancellationToken);
    }

    private sealed class WaveProbe
    {
        private readonly int[] _processed = new int[RequestCount];
        private int _handlerCalls;
        private int _activeHandlerCalls;
        private int _maximumActiveHandlerCalls;
        private int _maximumBatchSize;
        private int _downstreamDelayMilliseconds;

        public void Configure(int downstreamDelayMilliseconds)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(downstreamDelayMilliseconds);
            Volatile.Write(ref _downstreamDelayMilliseconds, downstreamDelayMilliseconds);
        }

        public void BeginWave()
        {
            if (Volatile.Read(ref _activeHandlerCalls) != 0)
            {
                throw new InvalidOperationException("A previous wave still has active handler calls.");
            }

            Array.Clear(_processed);
            Volatile.Write(ref _handlerCalls, 0);
            Volatile.Write(ref _activeHandlerCalls, 0);
            Volatile.Write(ref _maximumActiveHandlerCalls, 0);
            Volatile.Write(ref _maximumBatchSize, 0);
        }

        public ValueTask HandleAsync(
            IReadOnlyList<RequestBatchEntry<int, int>> entries,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _handlerCalls);
            var entryCount = entries.Count;
            UpdateMaximum(ref _maximumBatchSize, entryCount);
            var activeCalls = Interlocked.Increment(ref _activeHandlerCalls);
            UpdateMaximum(ref _maximumActiveHandlerCalls, activeCalls);
            try
            {
                for (var entryIndex = 0; entryIndex < entryCount; entryIndex++)
                {
                    var entry = entries[entryIndex];
                    var request = entry.Request;
                    if ((uint)request >= RequestCount)
                    {
                        throw new InvalidOperationException($"Received unexpected request id {request}.");
                    }

                    Interlocked.Increment(ref _processed[request]);
                }

                entries.SetResponses(static request => request * 17 + 3);
            }
            catch
            {
                Interlocked.Decrement(ref _activeHandlerCalls);
                throw;
            }

            var delayMilliseconds = Volatile.Read(ref _downstreamDelayMilliseconds);
            if (delayMilliseconds == 0)
            {
                Interlocked.Decrement(ref _activeHandlerCalls);
                return ValueTask.CompletedTask;
            }

            // Keep the execution slot occupied while the simulated downstream call is pending. This lets a burst
            // accumulate behind the bounded dispatcher and distinguishes slow-consumer behavior from the zero-delay
            // scheduling probe.
            return CompleteAfterDelayAsync(delayMilliseconds, cancellationToken);
        }

        private async ValueTask CompleteAfterDelayAsync(int delayMilliseconds, CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(delayMilliseconds, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref _activeHandlerCalls);
            }
        }

        public int ValidateWave()
        {
            for (var requestIndex = 0; requestIndex < RequestCount; requestIndex++)
            {
                var count = Volatile.Read(ref _processed[requestIndex]);
                if (count != 1)
                {
                    throw new InvalidOperationException(
                        $"Request {requestIndex} was handled {count} times; expected exactly once.");
                }
            }

            var maximumBatchSize = Volatile.Read(ref _maximumBatchSize);
            var maximumActiveHandlerCalls = Volatile.Read(ref _maximumActiveHandlerCalls);
            if (Volatile.Read(ref _activeHandlerCalls) != 0 ||
                maximumBatchSize is < 1 or > BatchSize ||
                maximumActiveHandlerCalls is < 1 or > MaxConcurrency)
            {
                throw new InvalidOperationException(
                    $"Observed dispatch bounds batch={maximumBatchSize}, " +
                    $"concurrency={maximumActiveHandlerCalls}; expected batch 1..{BatchSize} and " +
                    $"concurrency 1..{MaxConcurrency}.");
            }

            return Volatile.Read(ref _handlerCalls);
        }

        private static void UpdateMaximum(ref int location, int value)
        {
            var current = Volatile.Read(ref location);
            while (value > current)
            {
                var observed = Interlocked.CompareExchange(ref location, value, current);
                if (observed == current)
                {
                    return;
                }

                current = observed;
            }
        }
    }
}
