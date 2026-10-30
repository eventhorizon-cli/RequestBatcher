using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Engines;
using BenchmarkDotNet.Order;
using Microsoft.Extensions.DependencyInjection;

namespace RequestBatcher.Benchmarks.InMemory;

/// <summary>
/// Measures shared completion through the public dependency-injection request batcher.
/// </summary>
[MemoryDiagnoser]
[RankColumn]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
[SimpleJob(
    RunStrategy.Throughput,
    launchCount: 2,
    warmupCount: 5,
    iterationCount: 12,
    id: "ExplicitCompletion")]
[IterationTime(300)]
public class ExplicitCompletionBenchmarks
{
    private const int BatchSize = 32;
    private const int MaxConcurrency = 8;
    private const int PartitionCount = 4;
    private const int MaxRequestCount = 1_000;
    private const int BlockerRequestId = -1;

    private int[] _requests = [];
    private BatcherFixture<SuccessHandler>? _success;
    private BatcherFixture<FailureHandler>? _failure;
    private BatcherFixture<PartialCancellationHandler>? _partialCancellation;
    private BatcherFixture<CapacityHandler>? _capacity;
    private BatcherFixture<SuccessHandler>? _mixed;

    [Params(10, 1_000)]
    public int GroupSize { get; set; }

    private BatcherFixture<SuccessHandler> Success =>
        _success ?? throw new InvalidOperationException("The success batcher has not been initialized.");

    private BatcherFixture<FailureHandler> Failure =>
        _failure ?? throw new InvalidOperationException("The failure batcher has not been initialized.");

    private BatcherFixture<PartialCancellationHandler> PartialCancellation =>
        _partialCancellation ?? throw new InvalidOperationException(
            "The partial cancellation batcher has not been initialized.");

    private BatcherFixture<CapacityHandler> Capacity =>
        _capacity ?? throw new InvalidOperationException("The capacity batcher has not been initialized.");

    private BatcherFixture<SuccessHandler> Mixed =>
        _mixed ?? throw new InvalidOperationException("The mixed batcher has not been initialized.");

    [GlobalSetup]
    public void GlobalSetup()
    {
        _requests = Enumerable.Range(0, GroupSize).ToArray();

        _success = CreateBatcherFixture<SuccessHandler>(ConfigureStandard);
        _failure = CreateBatcherFixture<FailureHandler>(ConfigureStandard);
        _partialCancellation = CreateBatcherFixture<PartialCancellationHandler>(ConfigurePartialCancellation);
        _capacity = CreateBatcherFixture<CapacityHandler>(ConfigureCapacity);
        _mixed = CreateBatcherFixture<SuccessHandler>(ConfigureStandard);
    }

    [Benchmark(Description = "Explicit group success")]
    public async Task ExplicitGroupSuccessAsync()
    {
        Success.Handler.BeginWave();
        var operation = Success.Batcher.ExecuteAsync(_requests);
        var waiter = ObserveSuccessAsync(operation);
        Success.Handler.ReleaseWave();
        await waiter.ConfigureAwait(false);
        Success.Handler.ValidateProcessed(GroupSize, expectedCount: 1);
    }

    [Benchmark(Description = "Explicit group observed handler failure")]
    public async Task ExplicitGroupFailureAsync()
    {
        Failure.Handler.BeginWave();
        var operation = Failure.Batcher.ExecuteAsync(_requests);
        var waiter = ObserveExpectedFailureAsync(operation, Failure.Handler.Failure);
        Failure.Handler.ReleaseWave();
        await waiter.ConfigureAwait(false);
        Failure.Handler.ValidateProcessed(GroupSize, expectedCount: 1);
    }

    [Benchmark(Description = "Explicit group partial caller cancellation")]
    public async Task ExplicitGroupPartialCancellationAsync()
    {
        PartialCancellation.Handler.BeginWave();
        using var cancellation = new CancellationTokenSource();
        var operation = PartialCancellation.Batcher.ExecuteAsync(_requests, cancellation.Token);

        await PartialCancellation.Handler.FirstHandlerStarted.ConfigureAwait(false);
        var canceled = ObserveExpectedCancellationAsync(operation, cancellation.Token);
        cancellation.Cancel();
        PartialCancellation.Handler.ReleaseFirstHandler();

        await canceled.ConfigureAwait(false);

        var drain = PartialCancellation.Batcher.ExecuteAsync(BlockerRequestId);
        var drainWaiter = ObserveSuccessAsync(drain);
        await drainWaiter.ConfigureAwait(false);
        PartialCancellation.Handler.ValidatePartialProcessing(GroupSize);
    }

    [Benchmark(Description = "Explicit groups with capacity waiting")]
    public async Task ExplicitGroupsCapacityWaitAsync()
    {
        Capacity.Handler.BeginWave();
        var blocker = Capacity.Batcher.ExecuteAsync(BlockerRequestId);
        await Capacity.Handler.BlockerStarted.ConfigureAwait(false);

        var firstGroup = Capacity.Batcher.ExecuteAsync(_requests);
        var secondGroup = Capacity.Batcher.ExecuteAsync(_requests);
        if (secondGroup.IsCompleted)
        {
            throw new InvalidOperationException("The second group did not wait for queue capacity.");
        }

        var all = Task.WhenAll(blocker, firstGroup, secondGroup);
        var waiter = ObserveSuccessAsync(all);
        Capacity.Handler.ReleaseBlocker();
        await waiter.ConfigureAwait(false);
        Capacity.Handler.ValidateProcessed(GroupSize, expectedCount: 2);
    }

    [Benchmark(Description = "Mixed single requests and one explicit group")]
    public async Task MixedSinglesAndExplicitGroupAsync()
    {
        Mixed.Handler.BeginWave();
        var submissions = new Task[GroupSize + 1];
        for (var index = 0; index < GroupSize; index++)
        {
            submissions[index] = Mixed.Batcher.ExecuteAsync(_requests[index]);
        }

        submissions[GroupSize] = Mixed.Batcher.ExecuteAsync(_requests);
        var all = Task.WhenAll(submissions);
        var waiter = ObserveSuccessAsync(all);
        Mixed.Handler.ReleaseWave();
        await waiter.ConfigureAwait(false);
        Mixed.Handler.ValidateProcessed(GroupSize, expectedCount: 2);
    }

    [GlobalCleanup]
    public async Task GlobalCleanupAsync()
    {
        await DisposeBatcherFixtureAsync(_success).ConfigureAwait(false);
        await DisposeBatcherFixtureAsync(_failure).ConfigureAwait(false);
        await DisposeBatcherFixtureAsync(_partialCancellation).ConfigureAwait(false);
        await DisposeBatcherFixtureAsync(_capacity).ConfigureAwait(false);
        await DisposeBatcherFixtureAsync(_mixed).ConfigureAwait(false);
        _success = null;
        _failure = null;
        _partialCancellation = null;
        _capacity = null;
        _mixed = null;
    }

    private static void ConfigureStandard(RequestBatchOptions<int> options, int groupSize)
    {
        options.BatchSize = BatchSize;
        options.MaxConcurrency = MaxConcurrency;
        options.MaxPendingRequests = groupSize * 2;
        options.UsePartitionKey(static request => request % 64, PartitionCount);
    }

    private static void ConfigurePartialCancellation(RequestBatchOptions<int> options, int groupSize)
    {
        options.BatchSize = 1;
        options.MaxConcurrency = 1;
        options.MaxPendingRequests = groupSize * 2;
        options.UsePartitionKey(static _ => 0, PartitionCount);
    }

    private static void ConfigureCapacity(RequestBatchOptions<int> options, int groupSize)
    {
        options.BatchSize = BatchSize;
        options.MaxConcurrency = 1;
        options.MaxPendingRequests = groupSize;
        options.UsePartitionKey(static _ => 0, PartitionCount);
    }

    private BatcherFixture<THandler> CreateBatcherFixture<THandler>(Action<RequestBatchOptions<int>, int> configure)
        where THandler : class, IRequestBatchHandler<int>
    {
        var services = new ServiceCollection();
        services.AddRequestBatcher<int, THandler>(
            ServiceLifetime.Singleton,
            options => configure(options, GroupSize));

        var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });

        var handler = provider.GetRequiredService<IRequestBatchHandler<int>>() as THandler ??
            throw new InvalidOperationException("The benchmark handler was not resolved through its public interface.");
        var batcher = provider.GetRequiredService<IRequestBatcher<int>>();
        return new BatcherFixture<THandler>(provider, batcher, handler);
    }

    private static async Task ObserveSuccessAsync(Task operation) =>
        await operation.ConfigureAwait(false);

    private static async Task ObserveExpectedFailureAsync(Task operation, Exception expected)
    {
        try
        {
            await operation.ConfigureAwait(false);
        }
        catch (BenchmarkHandlerException exception) when (ReferenceEquals(exception, expected))
        {
            return;
        }

        throw new InvalidOperationException("The explicit group did not observe the original handler exception.");
    }

    private static async Task ObserveExpectedCancellationAsync(Task operation, CancellationToken expectedToken)
    {
        try
        {
            await operation.ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (exception.CancellationToken == expectedToken)
        {
            if (!operation.IsCanceled)
            {
                throw new InvalidOperationException("The cancellation task did not remain canceled.");
            }

            return;
        }

        throw new InvalidOperationException("The explicit group did not observe caller cancellation.");
    }

    private static async ValueTask DisposeBatcherFixtureAsync<THandler>(BatcherFixture<THandler>? fixture)
        where THandler : class, IRequestBatchHandler<int>
    {
        if (fixture is not null)
        {
            await fixture.DisposeAsync().ConfigureAwait(false);
        }
    }

    private sealed class BatcherFixture<THandler>(
        ServiceProvider provider,
        IRequestBatcher<int> batcher,
        THandler handler) : IAsyncDisposable
        where THandler : class, IRequestBatchHandler<int>
    {
        public ServiceProvider Provider { get; } = provider;

        public IRequestBatcher<int> Batcher { get; } = batcher;

        public THandler Handler { get; } = handler;

        public ValueTask DisposeAsync() => Provider.DisposeAsync();
    }

    private abstract class CountingHandler : IRequestBatchHandler<int>
    {
        private readonly int[] _counts = new int[MaxRequestCount];
        private TaskCompletionSource _release = CreateSource();

        public virtual void BeginWave()
        {
            Array.Clear(_counts);
            Volatile.Write(ref _release, CreateSource());
        }

        public void ReleaseWave() => Volatile.Read(ref _release).TrySetResult();

        public virtual async ValueTask HandleAsync(
            IReadOnlyList<int> requests,
            CancellationToken cancellationToken = default)
        {
            await Volatile.Read(ref _release).Task.ConfigureAwait(false);
            foreach (var request in requests)
            {
                if ((uint)request < MaxRequestCount)
                {
                    Interlocked.Increment(ref _counts[request]);
                }
            }

            OnBatchHandled();
        }

        public void ValidateProcessed(int requestCount, int expectedCount)
        {
            var total = 0;
            for (var request = 0; request < requestCount; request++)
            {
                var count = Volatile.Read(ref _counts[request]);
                if (count != expectedCount)
                {
                    throw new InvalidOperationException(
                        $"Request {request} was processed {count} times; expected {expectedCount}.");
                }

                total += count;
            }

            var expectedTotal = requestCount * expectedCount;
            if (total != expectedTotal)
            {
                throw new InvalidOperationException(
                    $"The handler processed {total} requests; expected {expectedTotal}.");
            }
        }

        protected virtual void OnBatchHandled()
        {
        }

        protected void Increment(int request) => Interlocked.Increment(ref _counts[request]);

        protected int CountAt(int request) => Volatile.Read(ref _counts[request]);

        private static TaskCompletionSource CreateSource() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class SuccessHandler : CountingHandler;

    private sealed class FailureHandler : CountingHandler
    {
        public BenchmarkHandlerException Failure { get; } = new();

        protected override void OnBatchHandled() => throw Failure;
    }

    private sealed class PartialCancellationHandler : CountingHandler
    {
        private readonly object _gateLock = new();
        private TaskCompletionSource _firstHandlerStarted = CreateSource();
        private TaskCompletionSource _releaseFirstHandler = CreateSource();

        public Task FirstHandlerStarted => Volatile.Read(ref _firstHandlerStarted).Task;

        public override void BeginWave()
        {
            base.BeginWave();
            lock (_gateLock)
            {
                _firstHandlerStarted = CreateSource();
                _releaseFirstHandler = CreateSource();
            }
        }

        public void ReleaseFirstHandler() => Volatile.Read(ref _releaseFirstHandler).TrySetResult();

        public void ValidatePartialProcessing(int requestCount)
        {
            if (CountAt(0) != 1)
            {
                throw new InvalidOperationException("The first request was not processed exactly once.");
            }

            for (var request = 1; request < requestCount; request++)
            {
                if (CountAt(request) != 0)
                {
                    throw new InvalidOperationException(
                        $"Canceled request {request} was processed unexpectedly.");
                }
            }
        }

        public override async ValueTask HandleAsync(
            IReadOnlyList<int> requests,
            CancellationToken cancellationToken = default)
        {
            if (requests.Count == 1 && requests[0] == BlockerRequestId)
            {
                return;
            }

            if (requests.Count != 1 || requests[0] != 0)
            {
                foreach (var request in requests)
                {
                    if ((uint)request < MaxRequestCount)
                    {
                        Increment(request);
                    }
                }

                return;
            }

            Volatile.Read(ref _firstHandlerStarted).TrySetResult();
            await Volatile.Read(ref _releaseFirstHandler).Task.ConfigureAwait(false);
            Increment(0);
        }

        private static TaskCompletionSource CreateSource() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class CapacityHandler : CountingHandler
    {
        private readonly object _gateLock = new();
        private TaskCompletionSource _blockerStarted = CreateSource();
        private TaskCompletionSource _releaseBlocker = CreateSource();

        public Task BlockerStarted => Volatile.Read(ref _blockerStarted).Task;

        public override void BeginWave()
        {
            base.BeginWave();
            ReleaseWave();
            lock (_gateLock)
            {
                _blockerStarted = CreateSource();
                _releaseBlocker = CreateSource();
            }
        }

        public void ReleaseBlocker() => Volatile.Read(ref _releaseBlocker).TrySetResult();

        public override async ValueTask HandleAsync(
            IReadOnlyList<int> requests,
            CancellationToken cancellationToken = default)
        {
            if (requests.Count == 1 && requests[0] == BlockerRequestId)
            {
                Volatile.Read(ref _blockerStarted).TrySetResult();
                await Volatile.Read(ref _releaseBlocker).Task.ConfigureAwait(false);
                return;
            }

            await base.HandleAsync(requests, cancellationToken).ConfigureAwait(false);
        }

        private static TaskCompletionSource CreateSource() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class BenchmarkHandlerException : Exception;
}
