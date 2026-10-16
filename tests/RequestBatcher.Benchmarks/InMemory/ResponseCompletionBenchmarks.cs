using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Engines;
using BenchmarkDotNet.Order;
using Microsoft.Extensions.DependencyInjection;

namespace RequestBatcher.Benchmarks.InMemory;

/// <summary>
/// Measures response completion through the public dependency-injection pipeline.
/// </summary>
[MemoryDiagnoser]
[RankColumn]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
[SimpleJob(
    RunStrategy.Throughput,
    launchCount: 2,
    warmupCount: 8,
    iterationCount: 20,
    id: "ResponseCompletion")]
[IterationTime(500)]
public class ResponseCompletionBenchmarks
{
    private const int RequestCount = 256;
    private const int BatchSize = 32;
    private const int MaxConcurrency = 8;
    private const int MaxPendingRequests = RequestCount * 2;
    private const int BlockerRequestId = -1;
    private const int DrainRequestId = -2;

    private ResponseWaveHandler? _responseHandler;
    private RequestWaveHandler? _requestHandler;
    private BlockedResponseHandler? _blockedResponseHandler;
    private readonly int[] _responseRequests = Enumerable.Range(0, RequestCount).ToArray();
    private readonly ControlRequest[] _requestOnlyRequests = Enumerable
        .Range(0, RequestCount)
        .Select(static value => new ControlRequest(value))
        .ToArray();
    private readonly BlockedRequest[] _blockedRequests = Enumerable
        .Range(0, RequestCount)
        .Select(static value => new BlockedRequest(value))
        .ToArray();
    private ServiceProvider? _serviceProvider;
    private IRequestBatcher<int, int>? _responseBatcher;
    private IRequestBatcher<ControlRequest>? _requestBatcher;
    private IRequestBatcher<BlockedRequest, int>? _blockedResponseBatcher;

    private IRequestBatcher<int, int> ResponseBatcher =>
        _responseBatcher ?? throw new InvalidOperationException("The benchmark has not been initialized.");

    private ResponseWaveHandler ResponseHandler =>
        _responseHandler ?? throw new InvalidOperationException("The benchmark has not been initialized.");

    private IRequestBatcher<ControlRequest> RequestBatcher =>
        _requestBatcher ?? throw new InvalidOperationException("The benchmark has not been initialized.");

    private RequestWaveHandler RequestHandler =>
        _requestHandler ?? throw new InvalidOperationException("The benchmark has not been initialized.");

    private IRequestBatcher<BlockedRequest, int> BlockedResponseBatcher =>
        _blockedResponseBatcher ?? throw new InvalidOperationException("The benchmark has not been initialized.");

    private BlockedResponseHandler CancellationHandler =>
        _blockedResponseHandler ?? throw new InvalidOperationException("The benchmark has not been initialized.");

    [GlobalSetup]
    public void GlobalSetup()
    {
        var services = new ServiceCollection();
        services.AddRequestBatcher<int, int, ResponseWaveHandler>(
            ServiceLifetime.Singleton,
            options => Configure(options, MaxConcurrency));
        services.AddRequestBatcher<ControlRequest, RequestWaveHandler>(
            ServiceLifetime.Singleton,
            options => Configure(options, MaxConcurrency));
        services.AddRequestBatcher<BlockedRequest, int, BlockedResponseHandler>(
            ServiceLifetime.Singleton,
            options =>
            {
                options.BatchSize = BatchSize;
                options.MaxConcurrency = 1;
                options.MaxPendingRequests = MaxPendingRequests;
            });

        _serviceProvider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });

        _responseHandler = _serviceProvider.GetRequiredService<IRequestBatchHandler<int, int>>() as ResponseWaveHandler ??
            throw new InvalidOperationException("The response benchmark handler was not resolved.");
        _requestHandler = _serviceProvider.GetRequiredService<IRequestBatchHandler<ControlRequest>>() as RequestWaveHandler ??
            throw new InvalidOperationException("The request benchmark handler was not resolved.");
        _blockedResponseHandler = _serviceProvider.GetRequiredService<IRequestBatchHandler<BlockedRequest, int>>() as
            BlockedResponseHandler ?? throw new InvalidOperationException("The cancellation benchmark handler was not resolved.");
        _responseBatcher = _serviceProvider.GetRequiredService<IRequestBatcher<int, int>>();
        _requestBatcher = _serviceProvider.GetRequiredService<IRequestBatcher<ControlRequest>>();
        _blockedResponseBatcher = _serviceProvider.GetRequiredService<IRequestBatcher<BlockedRequest, int>>();
    }

    [Benchmark(Description = "Request-only: single success control")]
    public async Task RequestOnlySingleSuccessAsync()
    {
        RequestHandler.BeginWave();

        var tasks = new Task[RequestCount];
        for (var index = 0; index < RequestCount; index++)
        {
            tasks[index] = RequestBatcher.ExecuteAsync(_requestOnlyRequests[index]);
        }

        var all = AwaitRequestsAsync(tasks);
        RequestHandler.ReleaseWave();
        await all.ConfigureAwait(false);

        if (RequestHandler.HandledCount != RequestCount)
        {
            throw new InvalidOperationException(
                $"The request-only handler processed {RequestHandler.HandledCount} requests; expected {RequestCount}.");
        }
    }

    [Benchmark(Description = "Response: single success")]
    public async Task ResponseSingleSuccessAsync()
    {
        ResponseHandler.BeginSuccessWave();

        var tasks = new Task<int>[RequestCount];
        for (var index = 0; index < RequestCount; index++)
        {
            tasks[index] = ResponseBatcher.ExecuteAsync(_responseRequests[index]);
        }

        var all = AwaitResponsesAsync(tasks);
        ResponseHandler.ReleaseWave();
        var responses = await all.ConfigureAwait(false);
        ValidateResponses(responses);
    }

    [Benchmark(Description = "Response: single failure")]
    public async Task ResponseSingleFailureAsync()
    {
        ResponseHandler.BeginFailureWave();

        var tasks = new Task<int>[RequestCount];
        for (var index = 0; index < RequestCount; index++)
        {
            tasks[index] = ResponseBatcher.ExecuteAsync(_responseRequests[index]);
        }

        var all = AwaitResponsesAsync(tasks);
        ResponseHandler.ReleaseWave();
        var failureObserved = false;
        try
        {
            await all.ConfigureAwait(false);
            failureObserved = false;
        }
        catch (Exception)
        {
            failureObserved = true;
        }

        if (!failureObserved)
        {
            throw new InvalidOperationException("The response failure wave unexpectedly succeeded.");
        }

        var observedFailures = 0;
        foreach (var task in tasks)
        {
            if (!task.IsFaulted || task.Exception is not { InnerExceptions.Count: 1 } aggregate ||
                aggregate.InnerExceptions[0] is not BenchmarkHandlerException)
            {
                throw new InvalidOperationException("A response failure task did not contain one handler exception.");
            }

            try
            {
                _ = await task.ConfigureAwait(false);
            }
            catch (BenchmarkHandlerException)
            {
                observedFailures++;
            }
        }

        if (observedFailures != RequestCount)
        {
            throw new InvalidOperationException(
                $"Observed {observedFailures} response failures; expected {RequestCount}.");
        }
    }

    [Benchmark(Description = "Response: pre-dispatch cancellation")]
    public async Task ResponsePreDispatchCancellationAsync()
    {
        CancellationHandler.BeginWave();
        var blocker = BlockedResponseBatcher.ExecuteAsync(new BlockedRequest(BlockerRequestId));
        try
        {
            await CancellationHandler.BlockerStarted.ConfigureAwait(false);

            using var cancellation = new CancellationTokenSource();
            var tasks = new Task<int>[RequestCount];
            for (var index = 0; index < RequestCount; index++)
            {
                tasks[index] = BlockedResponseBatcher.ExecuteAsync(_blockedRequests[index], cancellation.Token);
            }

            var all = AwaitResponsesAsync(tasks);
            cancellation.Cancel();
            var cancellationObserved = false;
            try
            {
                await all.ConfigureAwait(false);
                cancellationObserved = false;
            }
            catch (OperationCanceledException)
            {
                cancellationObserved = true;
            }

            if (!cancellationObserved)
            {
                throw new InvalidOperationException("The canceled response wave unexpectedly succeeded.");
            }

            var canceledCount = 0;
            foreach (var task in tasks)
            {
                try
                {
                    _ = await task.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    canceledCount++;
                }
            }

            if (canceledCount != RequestCount)
            {
                throw new InvalidOperationException(
                    $"Observed {canceledCount} canceled responses; expected {RequestCount}.");
            }
        }
        finally
        {
            CancellationHandler.ReleaseBlocker();
        }

        var drain = BlockedResponseBatcher.ExecuteAsync(new BlockedRequest(DrainRequestId));
        var blockerResponse = await blocker.ConfigureAwait(false);
        var drainResponse = await drain.ConfigureAwait(false);
        if (blockerResponse != BlockerRequestId || drainResponse != DrainRequestId)
        {
            throw new InvalidOperationException("The cancellation blocker or drain sentinel returned an invalid response.");
        }
    }

    [Benchmark(Description = "Response: explicit batch success")]
    public async Task ResponseExplicitBatchSuccessAsync()
    {
        ResponseHandler.BeginSuccessWave();
        var batch = ResponseBatcher.ExecuteAsync(_responseRequests);
        var all = AwaitBatchResponsesAsync(batch);
        ResponseHandler.ReleaseWave();
        var responses = await all.ConfigureAwait(false);
        ValidateResponses(responses);
    }

    [GlobalCleanup]
    public async Task GlobalCleanupAsync()
    {
        if (_serviceProvider is not null)
        {
            await _serviceProvider.DisposeAsync().ConfigureAwait(false);
            _serviceProvider = null;
        }
    }

    private static void Configure<TRequest>(RequestBatchOptions<TRequest> options, int maxConcurrency)
    {
        options.BatchSize = BatchSize;
        options.MaxConcurrency = maxConcurrency;
        options.MaxPendingRequests = MaxPendingRequests;
    }

    private static async Task<int[]> AwaitResponsesAsync(Task<int>[] tasks) =>
        await Task.WhenAll(tasks).ConfigureAwait(false);

    private static async Task<IReadOnlyList<int>> AwaitBatchResponsesAsync(Task<IReadOnlyList<int>> task) =>
        await task.ConfigureAwait(false);

    private static async Task AwaitRequestsAsync(Task[] tasks) =>
        await Task.WhenAll(tasks).ConfigureAwait(false);

    private static void ValidateResponses(IReadOnlyList<int> responses)
    {
        if (responses.Count != RequestCount)
        {
            throw new InvalidOperationException(
                $"Received {responses.Count} responses; expected {RequestCount}.");
        }

        for (var index = 0; index < responses.Count; index++)
        {
            if (responses[index] != index)
            {
                throw new InvalidOperationException(
                    $"Response at index {index} was {responses[index]}; expected {index}.");
            }
        }
    }

    private readonly record struct ControlRequest(int Value);

    private readonly record struct BlockedRequest(int Value);

    private sealed class ResponseWaveHandler
        : IRequestBatchHandler<int, int>
    {
        private readonly WaveGate _gate = new();
        private int _failure;

        public void BeginSuccessWave()
        {
            Volatile.Write(ref _failure, 0);
            _gate.Reset();
        }

        public void BeginFailureWave()
        {
            Volatile.Write(ref _failure, 1);
            _gate.Reset();
        }

        public void ReleaseWave() => _gate.Release();

        public async ValueTask HandleAsync(
            IReadOnlyList<RequestBatchEntry<int, int>> entries,
            CancellationToken cancellationToken = default)
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            if (Volatile.Read(ref _failure) != 0)
            {
                throw new BenchmarkHandlerException();
            }

            foreach (var entry in entries)
            {
                entry.SetResponse(entry.Request);
            }
        }
    }

    private sealed class RequestWaveHandler : IRequestBatchHandler<ControlRequest>
    {
        private readonly WaveGate _gate = new();
        private int _handledCount;

        public int HandledCount => Volatile.Read(ref _handledCount);

        public void BeginWave()
        {
            Volatile.Write(ref _handledCount, 0);
            _gate.Reset();
        }

        public void ReleaseWave() => _gate.Release();

        public async ValueTask HandleAsync(
            IReadOnlyList<ControlRequest> requests,
            CancellationToken cancellationToken = default)
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            Interlocked.Add(ref _handledCount, requests.Count);
        }
    }

    private sealed class BlockedResponseHandler : IRequestBatchHandler<BlockedRequest, int>
    {
        private readonly object _gateLock = new();
        private TaskCompletionSource _blockerStarted = CreateSource();
        private TaskCompletionSource _releaseBlocker = CreateSource();

        public Task BlockerStarted => Volatile.Read(ref _blockerStarted).Task;

        public void BeginWave()
        {
            lock (_gateLock)
            {
                _blockerStarted = CreateSource();
                _releaseBlocker = CreateSource();
            }
        }

        public void ReleaseBlocker() => Volatile.Read(ref _releaseBlocker).TrySetResult();

        public async ValueTask HandleAsync(
            IReadOnlyList<RequestBatchEntry<BlockedRequest, int>> entries,
            CancellationToken cancellationToken = default)
        {
            foreach (var entry in entries)
            {
                if (entry.Request.Value == BlockerRequestId)
                {
                    Volatile.Read(ref _blockerStarted).TrySetResult();
                    await Volatile.Read(ref _releaseBlocker).Task.ConfigureAwait(false);
                }

                entry.SetResponse(entry.Request.Value);
            }
        }
    }

    private sealed class BenchmarkHandlerException : Exception;

    private sealed class WaveGate
    {
        private TaskCompletionSource _release = CreateSource();

        public void Reset() => Volatile.Write(ref _release, CreateSource());

        public Task WaitAsync() => Volatile.Read(ref _release).Task;

        public void Release() => Volatile.Read(ref _release).TrySetResult();
    }

    private static TaskCompletionSource CreateSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
