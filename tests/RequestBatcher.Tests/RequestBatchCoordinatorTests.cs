using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace RequestBatcher.Tests;

public sealed class RequestBatchCoordinatorTests
{
    [Fact]
    public async Task ProcessAsync_ConcurrentRequests_MergesUpToBatchSize()
    {
        var firstBatchStarted = NewSource();
        var releaseFirstBatch = NewSource();
        var batches = new ConcurrentQueue<int[]>();
        var invocation = 0;

        await using var provider = CreateProvider<int>(async (requests, _) =>
        {
            batches.Enqueue(requests.ToArray());
            if (Interlocked.Increment(ref invocation) == 1)
            {
                firstBatchStarted.SetResult();
                await releaseFirstBatch.Task;
            }
        }, options => options.BatchSize = 10);
        var coordinator = provider.GetRequiredService<RequestBatchCoordinator<int>>();

        var first = coordinator.ProcessAsync(0);
        await firstBatchStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var followers = Enumerable.Range(1, 25)
            .Select(request => coordinator.ProcessAsync(request))
            .ToArray();

        releaseFirstBatch.SetResult();
        await Task.WhenAll(followers.Prepend(first)).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(26, batches.Sum(batch => batch.Length));
        Assert.All(batches, batch => Assert.InRange(batch.Length, 1, 10));
        Assert.Contains(batches, batch => batch.Length == 10);
    }

    [Fact]
    public async Task ProcessAsync_HandlerFails_PropagatesSameExceptionToWholeBatch()
    {
        var firstBatchStarted = NewSource();
        var releaseFirstBatch = NewSource();
        var invocation = 0;
        var expected = new TestBatchException();

        await using var provider = CreateProvider<int>(async (_, _) =>
        {
            if (Interlocked.Increment(ref invocation) == 1)
            {
                firstBatchStarted.SetResult();
                await releaseFirstBatch.Task;
                return;
            }

            throw expected;
        }, options => options.BatchSize = 32);
        var coordinator = provider.GetRequiredService<RequestBatchCoordinator<int>>();

        var leader = coordinator.ProcessAsync(0);
        await firstBatchStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var followers = Enumerable.Range(1, 16)
            .Select(request => coordinator.ProcessAsync(request))
            .ToArray();

        releaseFirstBatch.SetResult();
        await leader.WaitAsync(TimeSpan.FromSeconds(5));

        foreach (var follower in followers)
        {
            var actual = await Assert.ThrowsAsync<TestBatchException>(() => follower);
            Assert.Same(expected, actual);
        }
    }

    [Fact]
    public async Task ProcessAsync_MaxConcurrency_LimitsParallelHandlerInvocations()
    {
        const int concurrency = 3;
        var allPartitionsStarted = NewSource();
        var releasePartitions = NewSource();
        var active = 0;
        var maximumActive = 0;

        await using var provider = CreateProvider<int>(async (_, _) =>
        {
            var current = Interlocked.Increment(ref active);
            UpdateMaximum(ref maximumActive, current);
            if (current == concurrency)
            {
                allPartitionsStarted.TrySetResult();
            }

            await releasePartitions.Task;
            Interlocked.Decrement(ref active);
        }, options =>
        {
            options.BatchSize = 10;
            options.MaxConcurrency = concurrency;
        });
        var coordinator = provider.GetRequiredService<RequestBatchCoordinator<int>>();

        var requests = Enumerable.Range(0, concurrency)
            .Select(request => coordinator.ProcessAsync(request))
            .ToArray();

        await allPartitionsStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(concurrency, Volatile.Read(ref maximumActive));

        releasePartitions.SetResult();
        await Task.WhenAll(requests).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ProcessAsync_PartitionKey_EqualKeysCanExecuteConcurrently()
    {
        var firstKeyStarted = NewSource();
        var secondKeyStarted = NewSource();
        var releaseFirstKey = NewSource();

        await using var provider = CreateProvider<KeyedRequest>(async (requests, _) =>
        {
            var request = requests[0];
            if (request.Sequence == 1)
            {
                firstKeyStarted.SetResult();
                await releaseFirstKey.Task;
            }
            else
            {
                secondKeyStarted.SetResult();
            }
        }, options =>
        {
            options.BatchSize = 1;
            options.MaxConcurrency = 2;
            options.UsePartitionKey(request => request.Key);
        });
        var coordinator = provider.GetRequiredService<RequestBatchCoordinator<KeyedRequest>>();

        var first = coordinator.ProcessAsync(new KeyedRequest(1, 1));
        await firstKeyStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var secondForSameKey = coordinator.ProcessAsync(new KeyedRequest(1, 2));

        try
        {
            await secondKeyStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(first.IsCompleted);
        }
        finally
        {
            releaseFirstKey.TrySetResult();
        }

        await Task.WhenAll(first, secondForSameKey).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ProcessAsync_PartitionKeySelectorFails_PropagatesAndReleasesCapacity()
    {
        var expected = new TestBatchException();

        await using var provider = CreateProvider<int>(
            (_, _) => ValueTask.CompletedTask,
            options =>
            {
                options.MaxPendingRequests = 1;
                options.UsePartitionKey(request => request >= 0 ? request : throw expected);
            });
        var coordinator = provider.GetRequiredService<RequestBatchCoordinator<int>>();

        var actual = await Assert.ThrowsAsync<TestBatchException>(
            () => coordinator.ProcessAsync(-1));

        Assert.Same(expected, actual);
        await coordinator.ProcessAsync(1).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ProcessAsync_EmptyStringPartitionKey_ProcessesRequest()
    {
        var handled = new ConcurrentQueue<string>();

        await using var provider = CreateProvider<string>(
            (requests, _) =>
            {
                foreach (var request in requests)
                {
                    handled.Enqueue(request);
                }

                return ValueTask.CompletedTask;
            },
            options =>
            {
                options.MaxConcurrency = 2;
                options.UsePartitionKey(static request => request);
            });
        var coordinator = provider.GetRequiredService<RequestBatchCoordinator<string>>();

        await coordinator.ProcessAsync(string.Empty).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(string.Empty, Assert.Single(handled));
    }

    [Fact]
    public async Task ProcessAsync_RequestCanceledBeforeDispatch_SkipsRequest()
    {
        var firstBatchStarted = NewSource();
        var releaseFirstBatch = NewSource();
        var handled = new ConcurrentQueue<int>();

        await using var provider = CreateProvider<int>(async (requests, _) =>
        {
            foreach (var request in requests)
            {
                handled.Enqueue(request);
            }

            if (requests.Contains(1))
            {
                firstBatchStarted.SetResult();
                await releaseFirstBatch.Task;
            }
        });
        var coordinator = provider.GetRequiredService<RequestBatchCoordinator<int>>();

        var first = coordinator.ProcessAsync(1);
        await firstBatchStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        using var cancellation = new CancellationTokenSource();
        var canceled = coordinator.ProcessAsync(2, cancellation.Token);
        var unaffected = coordinator.ProcessAsync(3);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);
        releaseFirstBatch.SetResult();
        await Task.WhenAll(first, unaffected).WaitAsync(TimeSpan.FromSeconds(5));
        await coordinator.StopAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Contains(1, handled);
        Assert.DoesNotContain(2, handled);
        Assert.Contains(3, handled);
    }

    [Fact]
    public async Task ProcessAsync_RequestCanceledAfterDispatch_WaitsForActualOutcome()
    {
        var handlerStarted = NewSource();
        var releaseHandler = NewSource();

        await using var provider = CreateProvider<int>(async (_, _) =>
        {
            handlerStarted.SetResult();
            await releaseHandler.Task;
        });
        var coordinator = provider.GetRequiredService<RequestBatchCoordinator<int>>();
        using var cancellation = new CancellationTokenSource();

        var processing = coordinator.ProcessAsync(1, cancellation.Token);
        await handlerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        Assert.False(processing.IsCompleted);
        releaseHandler.SetResult();
        await processing.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ProcessAsync_HandlerReceivesConsumerToken_NotCallerToken()
    {
        var handlerStarted = new TaskCompletionSource<CancellationToken>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHandler = NewSource();

        await using var provider = CreateProvider<int>(async (_, cancellationToken) =>
        {
            handlerStarted.SetResult(cancellationToken);
            await releaseHandler.Task;
        });
        var coordinator = provider.GetRequiredService<RequestBatchCoordinator<int>>();
        using var callerCancellation = new CancellationTokenSource();

        var processing = coordinator.ProcessAsync(1, callerCancellation.Token);
        var handlerCancellation = await handlerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.NotEqual(callerCancellation.Token, handlerCancellation);
        callerCancellation.Cancel();
        Assert.False(handlerCancellation.IsCancellationRequested);
        Assert.False(processing.IsCompleted);

        releaseHandler.SetResult();
        await processing.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ProcessAsync_WaitModeAtCapacity_AppliesBackpressure()
    {
        var firstBatchStarted = NewSource();
        var releaseFirstBatch = NewSource();

        await using var provider = CreateProvider<int>(async (requests, _) =>
        {
            if (requests.Contains(1))
            {
                firstBatchStarted.SetResult();
                await releaseFirstBatch.Task;
            }
        }, options => options.MaxPendingRequests = 1);
        var coordinator = provider.GetRequiredService<RequestBatchCoordinator<int>>();

        var first = coordinator.ProcessAsync(1);
        await firstBatchStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var queued = coordinator.ProcessAsync(2);
        var waiting = coordinator.ProcessAsync(3);

        Assert.False(waiting.IsCompleted);

        releaseFirstBatch.SetResult();
        await Task.WhenAll(first, queued, waiting).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ProcessAsync_WaitModeCanceledWhileWaiting_CancelsRequest()
    {
        var firstBatchStarted = NewSource();
        var releaseFirstBatch = NewSource();

        await using var provider = CreateProvider<int>(async (_, _) =>
        {
            firstBatchStarted.TrySetResult();
            await releaseFirstBatch.Task;
        }, options => options.MaxPendingRequests = 1);
        var coordinator = provider.GetRequiredService<RequestBatchCoordinator<int>>();

        var first = coordinator.ProcessAsync(1);
        await firstBatchStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var queued = coordinator.ProcessAsync(2);
        using var cancellation = new CancellationTokenSource();
        var canceled = coordinator.ProcessAsync(3, cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);

        releaseFirstBatch.SetResult();
        await Task.WhenAll(first, queued).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ProcessAsync_FailModeQueueFull_RejectsImmediately()
    {
        var firstBatchStarted = NewSource();
        var releaseFirstBatch = NewSource();

        await using var provider = CreateProvider<int>(async (_, _) =>
        {
            firstBatchStarted.TrySetResult();
            await releaseFirstBatch.Task;
        }, options =>
        {
            options.MaxPendingRequests = 1;
            options.FullMode = RequestBatchFullMode.Fail;
        });
        var coordinator = provider.GetRequiredService<RequestBatchCoordinator<int>>();

        var first = coordinator.ProcessAsync(1);
        await firstBatchStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var queued = coordinator.ProcessAsync(2);
        var rejected = coordinator.ProcessAsync(3);

        var exception = await Assert.ThrowsAsync<RequestBatchQueueFullException>(() => rejected);
        Assert.Equal(1, exception.Capacity);

        releaseFirstBatch.SetResult();
        await Task.WhenAll(first, queued).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task StopAsync_SubmittedAndWaitingRequests_DrainsBothAndRejectsNew()
    {
        var handlerStarted = NewSource();
        var releaseHandler = NewSource();
        var handledRequests = new ConcurrentQueue<int>();

        await using var provider = CreateProvider<int>(async (requests, _) =>
        {
            var request = Assert.Single(requests);
            handledRequests.Enqueue(request);
            if (request == 1)
            {
                handlerStarted.SetResult();
                await releaseHandler.Task;
            }
        }, options => options.MaxPendingRequests = 1);
        var coordinator = provider.GetRequiredService<RequestBatchCoordinator<int>>();

        var first = coordinator.ProcessAsync(1);
        await handlerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = coordinator.ProcessAsync(2);
        var third = coordinator.ProcessAsync(3);
        var stopping = coordinator.StopAsync().AsTask();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => coordinator.ProcessAsync(4));
        Assert.False(second.IsCompleted);
        Assert.False(third.IsCompleted);
        Assert.False(stopping.IsCompleted);

        releaseHandler.SetResult();
        await Task.WhenAll(first, second, third, stopping).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { 1, 2, 3 }, handledRequests.Order());
    }

    [Fact]
    public async Task AddRequestBatcher_HandlerAndRegistrationCombined_UsesHandler()
    {
        var handler = new Mock<IRequestBatchHandler<int>>(MockBehavior.Strict);
        handler
            .Setup(candidate => candidate.HandleAsync(
                It.Is<IReadOnlyList<int>>(requests => requests.SequenceEqual(new[] { 42 })),
                It.Is<CancellationToken>(cancellationToken => cancellationToken.CanBeCanceled)))
            .Returns(ValueTask.CompletedTask)
            .Verifiable();

        var services = new ServiceCollection();
        services.AddSingleton<Func<IReadOnlyList<int>, CancellationToken, ValueTask>>(
            handler.Object.HandleAsync);
        services.AddRequestBatcher<int, TestBatchHandler<int>>(ServiceLifetime.Singleton);

        await using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        var batcher = provider.GetRequiredService<IRequestBatcher<int>>();
        var coordinator = provider.GetRequiredService<RequestBatchCoordinator<int>>();

        Assert.Same(coordinator, batcher);

        await batcher.ProcessAsync(42).WaitAsync(TimeSpan.FromSeconds(5));
        handler.Verify();
        handler.VerifyNoOtherCalls();
    }

    [Fact]
    public void AddRequestBatcher_SameRequestTypeRegisteredTwice_Throws()
    {
        var services = new ServiceCollection();
        services.AddSingleton<Func<IReadOnlyList<int>, CancellationToken, ValueTask>>(
            static (_, _) => ValueTask.CompletedTask);
        services.AddRequestBatcher<int, TestBatchHandler<int>>(ServiceLifetime.Singleton);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            services.AddRequestBatcher<int, TestBatchHandler<int>>(ServiceLifetime.Singleton));

        Assert.Contains(typeof(int).ToString(), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AddRequestBatcher_ScopedHandler_CreatesAndDisposesOneInstancePerBatch()
    {
        var probe = new HandlerLifetimeProbe();
        var services = new ServiceCollection();
        services.AddSingleton(probe);
        services.AddRequestBatcher<int, ScopedTestHandler>(
            ServiceLifetime.Scoped,
            options => options.BatchSize = 1);

        await using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        var batcher = provider.GetRequiredService<IRequestBatcher<int>>();

        await batcher.ProcessAsync(1).WaitAsync(TimeSpan.FromSeconds(5));
        await batcher.ProcessAsync(2).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, probe.HandlerIds.Count);
        Assert.Equal(2, probe.HandlerIds.Distinct().Count());
        Assert.Equal(2, probe.DisposeCount);
    }

    [Theory]
    [InlineData(ServiceLifetime.Singleton)]
    [InlineData(ServiceLifetime.Scoped)]
    [InlineData(ServiceLifetime.Transient)]
    public void AddRequestBatcher_HandlerLifetime_RegistersRequestedLifetime(
        ServiceLifetime handlerLifetime)
    {
        var services = new ServiceCollection();
        services.AddRequestBatcher<int, ScopedTestHandler>(handlerLifetime);

        var registration = Assert.Single(
            services,
            descriptor => descriptor.ServiceType == typeof(IRequestBatchHandler<int>));

        Assert.Equal(handlerLifetime, registration.Lifetime);
        Assert.Equal(typeof(ScopedTestHandler), registration.ImplementationType);
    }

    [Fact]
    public void AddRequestBatcher_Configure_RegistersOptionsAndInvokesCallbackOnce()
    {
        var configureCallCount = 0;
        var services = new ServiceCollection();
        services.AddSingleton<Func<IReadOnlyList<int>, CancellationToken, ValueTask>>(
            static (_, _) => ValueTask.CompletedTask);
        services.AddRequestBatcher<int, TestBatchHandler<int>>(
            ServiceLifetime.Singleton,
            options =>
            {
                configureCallCount++;
                options.BatchSize = 32;
                options.MaxConcurrency = 4;
                options.MaxPendingRequests = 512;
                options.FullMode = RequestBatchFullMode.Fail;
            });

        using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        var options = provider.GetRequiredService<IOptions<RequestBatchOptions<int>>>().Value;

        Assert.Equal(1, configureCallCount);
        Assert.Equal(32, options.BatchSize);
        Assert.Equal(4, options.MaxConcurrency);
        Assert.Equal(512, options.MaxPendingRequests);
        Assert.Equal(RequestBatchFullMode.Fail, options.FullMode);
    }

    [Theory]
    [InlineData(1, 12, 1)]
    [InlineData(4, 12, 4)]
    [InlineData(64, 12, 12)]
    [InlineData(4, 0, 1)]
    public void GetPartitionCount_MaxConcurrencyAndProcessorCount_AppliesBounds(
        int maxConcurrency,
        int processorCount,
        int expected)
    {
        var actual = RequestBatcherServiceCollectionExtensions.GetPartitionCount(
            maxConcurrency,
            processorCount);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(0, 1, 1, nameof(RequestBatchOptions<int>.BatchSize))]
    [InlineData(1, 0, 1, nameof(RequestBatchOptions<int>.MaxConcurrency))]
    [InlineData(1, 1, 0, nameof(RequestBatchOptions<int>.MaxPendingRequests))]
    public void AddRequestBatcher_InvalidOptions_Throws(
        int batchSize,
        int maxConcurrency,
        int maxPendingRequests,
        string parameterName)
    {
        var services = new ServiceCollection();

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            services.AddRequestBatcher<int, TestBatchHandler<int>>(
                ServiceLifetime.Singleton,
                options =>
                {
                    options.BatchSize = batchSize;
                    options.MaxConcurrency = maxConcurrency;
                    options.MaxPendingRequests = maxPendingRequests;
                }));

        Assert.Equal(parameterName, exception.ParamName);
    }

    [Fact]
    public async Task AddRequestBatcher_MultipleRequestTypes_ShareInternalQueue()
    {
        var integerHandler = new Mock<IRequestBatchHandler<int>>(MockBehavior.Strict);
        integerHandler
            .Setup(handler => handler.HandleAsync(
                It.Is<IReadOnlyList<int>>(requests => requests.SequenceEqual(new[] { 42 })),
                It.IsAny<CancellationToken>()))
            .Returns(ValueTask.CompletedTask);
        var stringHandler = new Mock<IRequestBatchHandler<string>>(MockBehavior.Strict);
        stringHandler
            .Setup(handler => handler.HandleAsync(
                It.Is<IReadOnlyList<string>>(requests => requests.SequenceEqual(new[] { "value" })),
                It.IsAny<CancellationToken>()))
            .Returns(ValueTask.CompletedTask);

        var services = new ServiceCollection();
        services.AddSingleton<Func<IReadOnlyList<int>, CancellationToken, ValueTask>>(
            integerHandler.Object.HandleAsync);
        services.AddSingleton<Func<IReadOnlyList<string>, CancellationToken, ValueTask>>(
            stringHandler.Object.HandleAsync);
        services.AddRequestBatcher<int, TestBatchHandler<int>>(ServiceLifetime.Singleton);
        services.AddRequestBatcher<string, TestBatchHandler<string>>(ServiceLifetime.Singleton);

        await using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        await Task.WhenAll(
            provider.GetRequiredService<IRequestBatcher<int>>().ProcessAsync(42),
            provider.GetRequiredService<IRequestBatcher<string>>().ProcessAsync("value"))
            .WaitAsync(TimeSpan.FromSeconds(5));

        integerHandler.VerifyAll();
        stringHandler.VerifyAll();
    }

    [Fact]
    public async Task ProcessAsync_HandlerFails_LogsOnceAndPropagatesOriginalException()
    {
        var expected = new TestBatchException();
        var logger = new RecordingLogger<RequestBatchCoordinator<int>>(LogLevel.Error);

        var services = new ServiceCollection();
        services.AddSingleton<ILogger<RequestBatchCoordinator<int>>>(logger);
        services.AddSingleton<Func<IReadOnlyList<int>, CancellationToken, ValueTask>>(
            (_, _) => ValueTask.FromException(expected));
        services.AddRequestBatcher<int, TestBatchHandler<int>>(ServiceLifetime.Singleton);

        await using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        var batcher = provider.GetRequiredService<IRequestBatcher<int>>();

        var actual = await Assert.ThrowsAsync<TestBatchException>(() => batcher.ProcessAsync(42));
        await logger.Logged.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Same(expected, actual);
        var logEntry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, logEntry.Level);
        Assert.Equal(1005, logEntry.EventId.Id);
        Assert.Contains("Failed to process", logEntry.Message, StringComparison.Ordinal);
        Assert.Same(expected, logEntry.Exception);
    }

    private static ServiceProvider CreateProvider<TRequest>(
        Func<IReadOnlyList<TRequest>, CancellationToken, ValueTask> handler,
        Action<RequestBatchOptions<TRequest>>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(handler);
        services.AddRequestBatcher<TRequest, TestBatchHandler<TRequest>>(
            ServiceLifetime.Singleton,
            configure);
        return services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    private static TaskCompletionSource NewSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static void UpdateMaximum(ref int maximum, int candidate)
    {
        var current = Volatile.Read(ref maximum);
        while (candidate > current)
        {
            var previous = Interlocked.CompareExchange(ref maximum, candidate, current);
            if (previous == current)
            {
                return;
            }

            current = previous;
        }
    }

    private sealed class TestBatchException : Exception;

    private sealed class TestBatchHandler<TRequest>(
        Func<IReadOnlyList<TRequest>, CancellationToken, ValueTask> handler)
        : IRequestBatchHandler<TRequest>
    {
        public ValueTask HandleAsync(
            IReadOnlyList<TRequest> requests,
            CancellationToken cancellationToken = default) =>
            handler(requests, cancellationToken);
    }

    private sealed record KeyedRequest(int Key, int Sequence);

    private sealed class HandlerLifetimeProbe
    {
        private int _disposeCount;

        public ConcurrentQueue<Guid> HandlerIds { get; } = new();

        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public void RecordDisposed() => Interlocked.Increment(ref _disposeCount);
    }

    private sealed class ScopedTestHandler(HandlerLifetimeProbe probe)
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

    private sealed class RecordingLogger<TCategory>(LogLevel enabledLevel) : ILogger<TCategory>
    {
        private readonly TaskCompletionSource _logged =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ConcurrentQueue<LogEntry> Entries { get; } = new();

        public Task Logged => _logged.Task;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull =>
            null;

        public bool IsEnabled(LogLevel logLevel) => logLevel == enabledLevel;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Enqueue(new LogEntry(logLevel, eventId, formatter(state, exception), exception));
            _logged.TrySetResult();
        }
    }

    private sealed record LogEntry(
        LogLevel Level,
        EventId EventId,
        string Message,
        Exception? Exception);
}
