using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;

namespace RequestBatcher.Tests;

public sealed class NamedRequestBatcherTests
{
    [Fact]
    public async Task AddRequestBatcher_NamedPipelinesWithSameRequestType_IsolatePipelinesAndOptions()
    {
        const string firstName = "price";
        const string secondName = "cost";
        var probe = new BatchProbe();
        var services = new ServiceCollection();
        services.AddSingleton(probe);
        services.AddRequestBatcher<int, RecordingHandler>(
            firstName,
            ServiceLifetime.Singleton,
            options => options.BatchSize = 1);
        services.AddRequestBatcher<int, RecordingHandler>(
            secondName,
            ServiceLifetime.Singleton,
            options => options.BatchSize = 2);

        await using var provider = BuildProvider(services);

        var first = provider.GetRequiredKeyedService<IRequestBatcher<int>>(firstName);
        var second = provider.GetRequiredKeyedService<IRequestBatcher<int>>(secondName);

        await Task.WhenAll(first.ExecuteAsync([1, 2]), second.ExecuteAsync([3, 4]))
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, probe.Batches.Count(batch => batch.SequenceEqual([1]) || batch.SequenceEqual([2])));
        Assert.Contains(probe.Batches, batch => batch.SequenceEqual([3, 4]));
        var options = provider.GetRequiredService<IOptionsMonitor<RequestBatchOptions<int>>>();
        Assert.Equal(1, options.Get(firstName).BatchSize);
        Assert.Equal(2, options.Get(secondName).BatchSize);
        Assert.NotSame(first, second);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AddRequestBatcher_NamedAndUnkeyedPipelines_CoexistInEitherRegistrationOrder(
        bool namedFirst)
    {
        const string firstName = "price";
        const string secondName = "cost";
        var probe = new BatchProbe();
        var services = new ServiceCollection();
        services.AddSingleton(probe);
        if (namedFirst)
        {
            services.AddRequestBatcher<int, RecordingHandler>(firstName, ServiceLifetime.Singleton);
            services.AddRequestBatcher<int, RecordingHandler>(secondName, ServiceLifetime.Singleton);
            services.AddRequestBatcher<int, RecordingHandler>(ServiceLifetime.Singleton);
        }
        else
        {
            services.AddRequestBatcher<int, RecordingHandler>(ServiceLifetime.Singleton);
            services.AddRequestBatcher<int, RecordingHandler>(firstName, ServiceLifetime.Singleton);
            services.AddRequestBatcher<int, RecordingHandler>(secondName, ServiceLifetime.Singleton);
        }

        await using var provider = BuildProvider(services);

        await Task.WhenAll(
                provider.GetRequiredService<IRequestBatcher<int>>().ExecuteAsync(1),
                provider.GetRequiredKeyedService<IRequestBatcher<int>>(firstName).ExecuteAsync(2),
                provider.GetRequiredKeyedService<IRequestBatcher<int>>(secondName).ExecuteAsync(3))
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal([1, 2, 3], probe.Batches.SelectMany(static batch => batch).Order());
    }

    [Fact]
    public void AddRequestBatcher_DuplicateNameAcrossVoidAndResponse_RejectsBeforeMutation()
    {
        const string name = "price";
        var services = new ServiceCollection();
        services.AddRequestBatcher<int, RecordingHandler>(name, ServiceLifetime.Singleton);
        var descriptorCount = services.Count;
        var configureCalls = 0;

        var exception = Assert.Throws<InvalidOperationException>(() =>
            services.AddRequestBatcher<int, string, RecordingStringResponseHandler>(
                name,
                ServiceLifetime.Singleton,
                _ => configureCalls++));

        Assert.Contains("already been registered", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, configureCalls);
        Assert.Equal(descriptorCount, services.Count);
    }

    [Fact]
    public void AddRequestBatcher_DuplicateResponseName_DoesNotChangeExistingRegistration()
    {
        const string name = "price";
        var services = new ServiceCollection();
        services.AddRequestBatcher<int, string, RecordingStringResponseHandler>(
            name,
            ServiceLifetime.Singleton,
            options => options.BatchSize = 7);
        var descriptorCount = services.Count;
        var configureCalls = 0;

        var exception = Assert.Throws<InvalidOperationException>(() =>
            services.AddRequestBatcher<int, string, RecordingStringResponseHandler>(
                name,
                ServiceLifetime.Singleton,
                _ => configureCalls++));

        Assert.Contains("already been registered", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, configureCalls);
        Assert.Equal(descriptorCount, services.Count);

        using var provider = BuildProvider(services);
        Assert.Equal(
            7,
            provider.GetRequiredService<IOptionsMonitor<RequestBatchOptions<int>>>().Get(name).BatchSize);
    }

    [Fact]
    public void AddRequestBatcher_InvalidName_LeavesServicesUnchanged()
    {
        foreach (var name in new string?[] { null, string.Empty, "   " })
        {
            var services = new ServiceCollection();
            var configureCalls = 0;

            var exception = Assert.ThrowsAny<ArgumentException>(() =>
                services.AddRequestBatcher<int, RecordingHandler>(
                    name!,
                    ServiceLifetime.Singleton,
                    _ => configureCalls++));

            Assert.Equal("batcherName", exception.ParamName);
            Assert.Equal(0, configureCalls);
            Assert.Empty(services);
        }
    }

    [Fact]
    public void AddRequestBatcher_InvalidLifetime_LeavesServicesUnchanged()
    {
        var services = new ServiceCollection();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            services.AddRequestBatcher<int, RecordingHandler>("price", (ServiceLifetime)99));

        Assert.Empty(services);
    }

    [Fact]
    public void AddRequestBatcher_InvalidOptions_LeavesServicesUnchanged()
    {
        var services = new ServiceCollection();
        var configureCalls = 0;

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            services.AddRequestBatcher<int, RecordingHandler>(
                "price",
                ServiceLifetime.Singleton,
                options =>
                {
                    configureCalls++;
                    options.BatchSize = 0;
                }));

        Assert.Equal(1, configureCalls);
        Assert.Empty(services);
    }

    [Fact]
    public async Task ExecuteAsync_NamedResponseBatch_ReturnsInputOrderAndSelectsOriginalRequests()
    {
        const string name = "price";
        var selected = new ConcurrentQueue<int>();
        var probe = new ResponseBatchProbe();
        var services = new ServiceCollection();
        services.AddSingleton(probe);
        services.AddRequestBatcher<int, int, TensResponseHandler>(
            name,
            ServiceLifetime.Singleton,
            options =>
            {
                options.BatchSize = 1;
                options.MaxConcurrency = 2;
                options.UsePartitionKey(
                    request =>
                    {
                        selected.Enqueue(request);
                        return request % 2;
                    },
                    partitionCount: 2);
            });

        await using var provider = BuildProvider(services);
        var batcher = provider.GetRequiredKeyedService<IRequestBatcher<int, int>>(name);

        var responses = await batcher.ExecuteAsync([3, 1, 2]).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal([30, 10, 20], responses);
        Assert.Equal([1, 2, 3], selected.Order());
        Assert.Equal([1, 2, 3], probe.Requests.Order());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExecuteAsync_NamedResponsePipelines_IsolatesHandlersAndUnkeyedRegistration(
        bool namedFirst)
    {
        const string tensName = "price";
        const string hundredsName = "bulk-price";
        var probe = new ResponseBatchProbe();
        var services = new ServiceCollection();
        services.AddSingleton(probe);

        if (namedFirst)
        {
            services.AddRequestBatcher<int, int, TensResponseHandler>(
                tensName,
                ServiceLifetime.Singleton);
            services.AddRequestBatcher<int, int, HundredsResponseHandler>(
                hundredsName,
                ServiceLifetime.Singleton);
            services.AddRequestBatcher<int, int, TensResponseHandler>(ServiceLifetime.Singleton);
        }
        else
        {
            services.AddRequestBatcher<int, int, TensResponseHandler>(ServiceLifetime.Singleton);
            services.AddRequestBatcher<int, int, TensResponseHandler>(
                tensName,
                ServiceLifetime.Singleton);
            services.AddRequestBatcher<int, int, HundredsResponseHandler>(
                hundredsName,
                ServiceLifetime.Singleton);
        }

        await using var provider = BuildProvider(services);
        var requests = new[] { 7, 7, 2 };

        var actual = await Task.WhenAll(
                provider.GetRequiredKeyedService<IRequestBatcher<int, int>>(tensName)
                    .ExecuteAsync(requests),
                provider.GetRequiredKeyedService<IRequestBatcher<int, int>>(hundredsName)
                    .ExecuteAsync(requests),
                provider.GetRequiredService<IRequestBatcher<int, int>>().ExecuteAsync(requests))
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(new[] { 70, 70, 20 }, actual[0]);
        Assert.Equal(new[] { 700, 700, 200 }, actual[1]);
        Assert.Equal(new[] { 70, 70, 20 }, actual[2]);
    }

    [Fact]
    public void AddRequestBatcher_DifferentResponseTypeSameName_RejectsBeforeMutation()
    {
        const string name = "price";
        var services = new ServiceCollection();
        services.AddRequestBatcher<int, string, RecordingStringResponseHandler>(
            name,
            ServiceLifetime.Singleton);
        var descriptorCount = services.Count;
        var configureCalls = 0;

        var exception = Assert.Throws<InvalidOperationException>(() =>
            services.AddRequestBatcher<int, int, TensResponseHandler>(
                name,
                ServiceLifetime.Singleton,
                _ => configureCalls++));

        Assert.Contains("already been registered", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, configureCalls);
        Assert.Equal(descriptorCount, services.Count);
    }

    [Fact]
    public void AddRequestBatcher_ResponseThenVoidSameName_RejectsBeforeMutation()
    {
        const string name = "price";
        var services = new ServiceCollection();
        services.AddRequestBatcher<int, string, RecordingStringResponseHandler>(
            name,
            ServiceLifetime.Singleton);
        var descriptorCount = services.Count;
        var configureCalls = 0;

        var exception = Assert.Throws<InvalidOperationException>(() =>
            services.AddRequestBatcher<int, RecordingHandler>(
                name,
                ServiceLifetime.Singleton,
                _ => configureCalls++));

        Assert.Contains("already been registered", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, configureCalls);
        Assert.Equal(descriptorCount, services.Count);
    }

    [Fact]
    public void AddRequestBatcher_NamedOptions_ConfigureOnceAndKeepDefaultIndependent()
    {
        const string name = "price";
        var configureCalls = 0;
        var services = new ServiceCollection();
        services.AddSingleton(new BatchProbe());
        services.AddRequestBatcher<int, string, RecordingStringResponseHandler>(
            name,
            ServiceLifetime.Singleton,
            options =>
            {
                configureCalls++;
                options.BatchSize = 32;
                options.MaxConcurrency = 4;
                options.MaxPendingRequests = 512;
                options.FullMode = RequestBatchFullMode.Fail;
            });
        services.AddRequestBatcher<int, RecordingHandler>(
            ServiceLifetime.Singleton,
            options => options.BatchSize = 5);

        using var provider = BuildProvider(services);
        using var scope = provider.CreateScope();
        var options = scope.ServiceProvider
            .GetRequiredService<IOptionsSnapshot<RequestBatchOptions<int>>>();

        Assert.Equal(1, configureCalls);
        Assert.Equal(32, options.Get(name).BatchSize);
        Assert.Equal(4, options.Get(name).MaxConcurrency);
        Assert.Equal(512, options.Get(name).MaxPendingRequests);
        Assert.Equal(RequestBatchFullMode.Fail, options.Get(name).FullMode);
        Assert.Equal(5, options.Get(Options.DefaultName).BatchSize);
    }

    [Theory]
    [InlineData(ServiceLifetime.Singleton, 1, 1)]
    [InlineData(ServiceLifetime.Scoped, 2, 2)]
    [InlineData(ServiceLifetime.Transient, 2, 2)]
    public async Task AddRequestBatcher_NamedResponseHandlerLifetime_ResolvesAndDisposesPerBatch(
        ServiceLifetime lifetime,
        int expectedHandlerCount,
        int expectedDisposeCount)
    {
        const string name = "price";
        var probe = new ResponseLifetimeProbe();
        var services = new ServiceCollection();
        services.AddSingleton(probe);
        services.AddRequestBatcher<int, string, ResponseLifetimeHandler>(
            name,
            lifetime,
            options => options.BatchSize = 1);

        await using (var provider = BuildProvider(services))
        {
            var batcher = provider.GetRequiredKeyedService<IRequestBatcher<int, string>>(name);
            Assert.Equal("response-1", await batcher.ExecuteAsync(1).WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal("response-2", await batcher.ExecuteAsync(2).WaitAsync(TimeSpan.FromSeconds(5)));
        }

        Assert.Equal(expectedHandlerCount, probe.HandlerIds.Distinct().Count());
        Assert.Equal(expectedDisposeCount, probe.DisposeCount);
    }

    [Fact]
    public async Task ExecuteAsync_NamedHandlerFailure_IsolatedFromOtherName()
    {
        const string failingName = "price";
        const string successfulName = "cost";
        var failure = new NamedBatchException();
        var probe = new BatchProbe { Failure = failure };
        var services = new ServiceCollection();
        services.AddSingleton(probe);
        services.AddRequestBatcher<int, FailingHandler>(failingName, ServiceLifetime.Singleton);
        services.AddRequestBatcher<int, RecordingHandler>(successfulName, ServiceLifetime.Singleton);

        await using var provider = BuildProvider(services);
        var failed = provider.GetRequiredKeyedService<IRequestBatcher<int>>(failingName).ExecuteAsync(1);
        var succeeded = provider.GetRequiredKeyedService<IRequestBatcher<int>>(successfulName).ExecuteAsync(2);

        await succeeded.WaitAsync(TimeSpan.FromSeconds(5));
        var actual = await Assert.ThrowsAsync<NamedBatchException>(() => failed);

        Assert.Same(failure, actual);
        Assert.Contains(probe.Batches, batch => batch.SequenceEqual([2]));
    }

    [Fact]
    public async Task ExecuteAsync_NamedFailModeBackpressure_IsolatedFromOtherName()
    {
        const string blockedName = "price";
        const string availableName = "cost";
        var probe = new BatchProbe();
        var services = new ServiceCollection();
        services.AddSingleton(probe);
        services.AddRequestBatcher<int, RecordingHandler>(
            blockedName,
            ServiceLifetime.Singleton,
            options =>
            {
                options.BatchSize = 1;
                options.MaxPendingRequests = 1;
                options.FullMode = RequestBatchFullMode.Fail;
            });
        services.AddRequestBatcher<int, RecordingHandler>(availableName, ServiceLifetime.Singleton);

        await using var provider = BuildProvider(services);
        var blocked = provider.GetRequiredKeyedService<IRequestBatcher<int>>(blockedName);
        var available = provider.GetRequiredKeyedService<IRequestBatcher<int>>(availableName);

        var exception = await Assert.ThrowsAsync<RequestBatchQueueFullException>(
            () => blocked.ExecuteAsync([1, 2]));
        await available.ExecuteAsync(3).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, exception.Capacity);
        Assert.Equal(2, exception.RequestedCount);
        Assert.Contains(probe.Batches, batch => batch.SequenceEqual([3]));
        Assert.DoesNotContain(probe.Batches, batch => batch.Contains(1) || batch.Contains(2));
    }

    [Fact]
    public async Task DisposeAsync_NamedCoordinator_DoesNotStopOtherPipeline()
    {
        const string disposedName = "price";
        const string activeName = "cost";
        var probe = new BlockingProbe();
        var services = new ServiceCollection();
        services.AddSingleton(probe);
        services.AddSingleton(new BatchProbe());
        services.AddRequestBatcher<int, BlockingHandler>(disposedName, ServiceLifetime.Singleton);
        services.AddRequestBatcher<int, RecordingHandler>(activeName, ServiceLifetime.Singleton);

        await using var provider = BuildProvider(services);
        var disposedBatcher = provider.GetRequiredKeyedService<IRequestBatcher<int>>(disposedName);
        var activeBatcher = provider.GetRequiredKeyedService<IRequestBatcher<int>>(activeName);
        var disposedCoordinator = provider.GetRequiredKeyedService<RequestBatchCoordinator<int>>(disposedName);

        var pending = disposedBatcher.ExecuteAsync(1);
        Task? disposal = null;
        try
        {
            await probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            disposal = disposedCoordinator.DisposeAsync().AsTask();

            await Assert.ThrowsAsync<ObjectDisposedException>(() => disposedBatcher.ExecuteAsync(2));
            await activeBatcher.ExecuteAsync(3).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(disposal.IsCompleted);
        }
        finally
        {
            probe.Release.TrySetResult();
        }

        await Task.WhenAll(pending, disposal!).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData(ServiceLifetime.Singleton, 1, 1)]
    [InlineData(ServiceLifetime.Scoped, 2, 2)]
    [InlineData(ServiceLifetime.Transient, 2, 2)]
    public async Task AddRequestBatcher_NamedHandlerLifetime_ResolvesAndDisposesPerBatch(
        ServiceLifetime lifetime,
        int expectedHandlerCount,
        int expectedDisposeCount)
    {
        const string name = "price";
        var probe = new LifetimeProbe();
        var services = new ServiceCollection();
        services.AddSingleton(probe);
        services.AddRequestBatcher<int, LifetimeHandler>(
            name,
            lifetime,
            options => options.BatchSize = 1);

        await using (var provider = BuildProvider(services))
        {
            var batcher = provider.GetRequiredKeyedService<IRequestBatcher<int>>(name);
            await batcher.ExecuteAsync(1).WaitAsync(TimeSpan.FromSeconds(5));
            await batcher.ExecuteAsync(2).WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.Equal(expectedHandlerCount, probe.HandlerIds.Distinct().Count());
        Assert.Equal(expectedDisposeCount, probe.DisposeCount);
    }

    [Fact]
    public async Task AddRequestBatcher_NamedHandlerInvocation_UsesStrictDependencyInteraction()
    {
        const string name = "price";
        var observer = new Mock<IHandlerObserver>(MockBehavior.Strict);
        observer
            .Setup(item => item.Record(It.Is<IReadOnlyList<int>>(requests => requests.SequenceEqual(new[] { 42 }))))
            .Verifiable();
        var services = new ServiceCollection();
        services.AddSingleton(observer.Object);
        services.AddRequestBatcher<int, ObservingHandler>(name, ServiceLifetime.Singleton);

        await using var provider = BuildProvider(services);
        await provider.GetRequiredKeyedService<IRequestBatcher<int>>(name)
            .ExecuteAsync(42)
            .WaitAsync(TimeSpan.FromSeconds(5));

        observer.Verify();
        observer.VerifyNoOtherCalls();
    }

    private static ServiceProvider BuildProvider(IServiceCollection services) =>
        services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });

    private sealed class BatchProbe
    {
        public ConcurrentQueue<int[]> Batches { get; } = new();

        public Exception? Failure { get; init; }

        public void Record(IReadOnlyList<int> requests) => Batches.Enqueue(requests.ToArray());
    }

    private sealed class RecordingHandler(BatchProbe probe) : IRequestBatchHandler<int>
    {
        public ValueTask HandleAsync(
            IReadOnlyList<int> requests,
            CancellationToken cancellationToken = default)
        {
            probe.Record(requests);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FailingHandler(BatchProbe probe) : IRequestBatchHandler<int>
    {
        public ValueTask HandleAsync(
            IReadOnlyList<int> requests,
            CancellationToken cancellationToken = default)
        {
            probe.Record(requests);
            return ValueTask.FromException(probe.Failure ?? new NamedBatchException());
        }
    }

    private sealed class ResponseBatchProbe
    {
        public ConcurrentQueue<int> Requests { get; } = new();
    }

    private sealed class ResponseLifetimeProbe
    {
        private int _disposeCount;

        public ConcurrentQueue<Guid> HandlerIds { get; } = new();

        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public void RecordDisposed() => Interlocked.Increment(ref _disposeCount);
    }

    private sealed class TensResponseHandler(ResponseBatchProbe probe)
        : IRequestBatchHandler<int, int>
    {
        public ValueTask HandleAsync(
            IReadOnlyList<RequestBatchEntry<int, int>> entries,
            CancellationToken cancellationToken = default)
        {
            foreach (var entry in entries)
            {
                probe.Requests.Enqueue(entry.Request);
                entry.SetResponse(entry.Request * 10);
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class HundredsResponseHandler(ResponseBatchProbe probe)
        : IRequestBatchHandler<int, int>
    {
        public ValueTask HandleAsync(
            IReadOnlyList<RequestBatchEntry<int, int>> entries,
            CancellationToken cancellationToken = default)
        {
            foreach (var entry in entries)
            {
                probe.Requests.Enqueue(entry.Request);
                entry.SetResponse(entry.Request * 100);
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingStringResponseHandler : IRequestBatchHandler<int, string>
    {
        public ValueTask HandleAsync(
            IReadOnlyList<RequestBatchEntry<int, string>> entries,
            CancellationToken cancellationToken = default)
        {
            foreach (var entry in entries)
            {
                entry.SetResponse(entry.Request.ToString());
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class ResponseLifetimeHandler(ResponseLifetimeProbe probe)
        : IRequestBatchHandler<int, string>, IAsyncDisposable
    {
        private readonly Guid _id = Guid.NewGuid();

        public ValueTask HandleAsync(
            IReadOnlyList<RequestBatchEntry<int, string>> entries,
            CancellationToken cancellationToken = default)
        {
            probe.HandlerIds.Enqueue(_id);
            foreach (var entry in entries)
            {
                entry.SetResponse($"response-{entry.Request}");
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            probe.RecordDisposed();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class BlockingProbe
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class BlockingHandler(BlockingProbe probe) : IRequestBatchHandler<int>
    {
        public async ValueTask HandleAsync(
            IReadOnlyList<int> requests,
            CancellationToken cancellationToken = default)
        {
            if (requests.Contains(1))
            {
                probe.Started.TrySetResult();
                await probe.Release.Task.ConfigureAwait(false);
            }
        }
    }

    private sealed class LifetimeProbe
    {
        private int _disposeCount;

        public ConcurrentQueue<Guid> HandlerIds { get; } = new();

        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public void RecordDisposed() => Interlocked.Increment(ref _disposeCount);
    }

    private sealed class LifetimeHandler(LifetimeProbe probe)
        : IRequestBatchHandler<int>, IAsyncDisposable
    {
        private readonly Guid _id = Guid.NewGuid();

        public ValueTask HandleAsync(
            IReadOnlyList<int> requests,
            CancellationToken cancellationToken = default)
        {
            probe.HandlerIds.Enqueue(_id);
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            probe.RecordDisposed();
            return ValueTask.CompletedTask;
        }
    }

    public interface IHandlerObserver
    {
        void Record(IReadOnlyList<int> requests);
    }

    private sealed class ObservingHandler(IHandlerObserver observer) : IRequestBatchHandler<int>
    {
        public ValueTask HandleAsync(
            IReadOnlyList<int> requests,
            CancellationToken cancellationToken = default)
        {
            observer.Record(requests);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class NamedBatchException : Exception;
}
