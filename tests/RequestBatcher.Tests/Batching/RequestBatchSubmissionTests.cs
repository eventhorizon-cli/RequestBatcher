using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;

namespace RequestBatcher.Tests.Batching;

public sealed class RequestBatchSubmissionTests
{
    [Fact]
    public async Task ExecuteAsync_MultipleRequests_ProcessesOneProducedBatch()
    {
        var handledBatches = new ConcurrentQueue<int[]>();

        await using var provider = CreateProvider<int>(
            (requests, _) =>
            {
                handledBatches.Enqueue(requests.ToArray());
                return ValueTask.CompletedTask;
            },
            options => options.BatchSize = 10);
        var batcher = provider.GetRequiredService<IRequestBatcher<int>>();

        await batcher.ExecuteAsync([1, 2, 3]).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(new[] { 1, 2, 3 }, Assert.Single(handledBatches));
    }

    [Fact]
    public async Task ExecuteAsync_EmptySequence_CompletesWithoutHandling()
    {
        var invocationCount = 0;

        await using var provider = CreateProvider<int>((_, _) =>
        {
            Interlocked.Increment(ref invocationCount);
            return ValueTask.CompletedTask;
        });
        var batcher = provider.GetRequiredService<IRequestBatcher<int>>();

        await batcher.ExecuteAsync([]).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, Volatile.Read(ref invocationCount));
    }

    [Fact]
    public async Task ExecuteAsync_HandlerFails_CompletesAllRequestsAndPropagatesException()
    {
        var expected = new TestBatchException();
        var handledRequests = new ConcurrentQueue<int>();

        await using var provider = CreateProvider<int>(
            (requests, _) =>
            {
                foreach (var request in requests)
                {
                    handledRequests.Enqueue(request);
                }

                return requests.Contains(3)
                    ? ValueTask.FromException(expected)
                    : ValueTask.CompletedTask;
            },
            options =>
            {
                options.BatchSize = 2;
                options.MaxPendingRequests = 2;
            });
        var batcher = provider.GetRequiredService<IRequestBatcher<int>>();

        var actual = await Assert.ThrowsAsync<TestBatchException>(
            () => batcher.ExecuteAsync(Enumerable.Range(1, 5)));

        Assert.Same(expected, actual);
        Assert.Equal(Enumerable.Range(1, 5), handledRequests.Order());
    }

    [Fact]
    public async Task ExecuteAsync_WaitModeBatchExceedsCapacity_ProcessesCapacitySizedSlices()
    {
        var firstSliceStarted = NewSource();
        var releaseFirstSlice = NewSource();
        var handledBatches = new ConcurrentQueue<int[]>();

        await using var provider = CreateProvider<int>(
            async (requests, _) =>
            {
                var batch = requests.ToArray();
                handledBatches.Enqueue(batch);
                if (batch.SequenceEqual(new[] { 1, 2 }))
                {
                    firstSliceStarted.SetResult();
                    await releaseFirstSlice.Task;
                }
            },
            options =>
            {
                options.BatchSize = 2;
                options.MaxPendingRequests = 2;
            });
        var batcher = provider.GetRequiredService<IRequestBatcher<int>>();

        var processing = batcher.ExecuteAsync([1, 2, 3]);
        await firstSliceStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(processing.IsCompleted);
        releaseFirstSlice.SetResult();
        await processing.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, handledBatches.Count);
        Assert.Equal(new[] { 1, 2 }, handledBatches.ElementAt(0));
        Assert.Equal(new[] { 3 }, handledBatches.ElementAt(1));
    }

    [Fact]
    public async Task ExecuteAsync_FailModeBatchExceedsCapacity_RejectsWithoutHandling()
    {
        var invocationCount = 0;

        await using var provider = CreateProvider<int>(
            (_, _) =>
            {
                Interlocked.Increment(ref invocationCount);
                return ValueTask.CompletedTask;
            },
            options =>
            {
                options.MaxPendingRequests = 2;
                options.FullMode = RequestBatchFullMode.Fail;
            });
        var batcher = provider.GetRequiredService<IRequestBatcher<int>>();

        var exception = await Assert.ThrowsAsync<RequestBatchQueueFullException>(
            () => batcher.ExecuteAsync([1, 2, 3]));

        Assert.Equal(2, exception.Capacity);
        Assert.Equal(3, exception.RequestedCount);
        Assert.Equal(0, Volatile.Read(ref invocationCount));
    }

    [Fact]
    public async Task ExecuteAsync_WaitModeOversizedBatchCanceledAfterDispatch_WaitsForActiveHandler()
    {
        var firstSliceStarted = NewSource();
        var releaseFirstSlice = NewSource();
        var handledBatches = new ConcurrentQueue<int[]>();

        await using var provider = CreateProvider<int>(
            async (requests, _) =>
            {
                handledBatches.Enqueue(requests.ToArray());
                firstSliceStarted.SetResult();
                await releaseFirstSlice.Task;
            },
            options =>
            {
                options.BatchSize = 2;
                options.MaxPendingRequests = 2;
            });
        var batcher = provider.GetRequiredService<IRequestBatcher<int>>();
        var coordinator = provider.GetRequiredService<RequestBatchCoordinator<int>>();
        using var cancellation = new CancellationTokenSource();

        var processing = batcher.ExecuteAsync([1, 2, 3], cancellation.Token);
        await firstSliceStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        var stopping = coordinator.DisposeAsync().AsTask();

        Assert.False(processing.IsCompleted);
        Assert.False(stopping.IsCompleted);

        releaseFirstSlice.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => processing.WaitAsync(TimeSpan.FromSeconds(5)));
        await stopping.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(new[] { 1, 2 }, Assert.Single(handledBatches));
    }

    [Fact]
    public async Task DisposeAsync_WaitModeOversizedBatch_DrainsAcceptedSlicesAndWaitingTail()
    {
        var firstRequestStarted = NewSource();
        var releaseFirstRequest = NewSource();
        var handledRequests = new ConcurrentQueue<int>();

        await using var provider = CreateProvider<int>(
            async (requests, _) =>
            {
                var request = Assert.Single(requests);
                handledRequests.Enqueue(request);
                if (request == 1)
                {
                    firstRequestStarted.SetResult();
                    await releaseFirstRequest.Task;
                }
            },
            options =>
            {
                options.BatchSize = 1;
                options.MaxPendingRequests = 4;
            });
        var coordinator = provider.GetRequiredService<RequestBatchCoordinator<int>>();

        var processing = coordinator.ExecuteAsync([1, 2, 3, 4, 5]);
        await firstRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stopping = coordinator.DisposeAsync().AsTask();

        Assert.False(processing.IsCompleted);
        Assert.False(stopping.IsCompleted);

        releaseFirstRequest.SetResult();
        await Task.WhenAll(processing, stopping).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, handledRequests);
    }

    [Fact]
    public async Task ExecuteAsync_FailModeHasInsufficientCapacity_RejectsWholeBatch()
    {
        var firstBatchStarted = NewSource();
        var releaseFirstBatch = NewSource();
        var handledRequests = new ConcurrentQueue<int>();

        await using var provider = CreateProvider<int>(
            async (requests, _) =>
            {
                foreach (var request in requests)
                {
                    handledRequests.Enqueue(request);
                }

                if (requests.Contains(0))
                {
                    firstBatchStarted.SetResult();
                    await releaseFirstBatch.Task;
                }
            },
            options =>
            {
                options.MaxPendingRequests = 2;
                options.FullMode = RequestBatchFullMode.Fail;
            });
        var batcher = provider.GetRequiredService<IRequestBatcher<int>>();

        var first = batcher.ExecuteAsync(0);
        await firstBatchStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var queuedFirst = batcher.ExecuteAsync(1);
        var queuedSecond = batcher.ExecuteAsync(2);

        try
        {
            var exception = await Assert.ThrowsAsync<RequestBatchQueueFullException>(
                () => batcher.ExecuteAsync([3, 4]));

            Assert.Equal(2, exception.Capacity);
            Assert.Equal(2, exception.RequestedCount);
            Assert.Equal(new[] { 0 }, handledRequests);
        }
        finally
        {
            releaseFirstBatch.TrySetResult();
        }

        await Task.WhenAll(first, queuedFirst, queuedSecond).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ExecuteAsync_WaitModeHasInsufficientCapacity_WaitsForWholeBatch()
    {
        var firstBatchStarted = NewSource();
        var releaseFirstBatch = NewSource();
        var handledBatches = new ConcurrentQueue<int[]>();

        await using var provider = CreateProvider<int>(
            async (requests, _) =>
            {
                handledBatches.Enqueue(requests.ToArray());
                if (requests.Contains(0))
                {
                    firstBatchStarted.SetResult();
                    await releaseFirstBatch.Task;
                }
            },
            options =>
            {
                options.BatchSize = 10;
                options.MaxPendingRequests = 2;
            });
        var batcher = provider.GetRequiredService<IRequestBatcher<int>>();

        var first = batcher.ExecuteAsync(0);
        await firstBatchStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var queuedBatch = batcher.ExecuteAsync([1, 2]);
        var waitingBatch = batcher.ExecuteAsync([3, 4]);

        Assert.False(waitingBatch.IsCompleted);
        releaseFirstBatch.SetResult();
        await Task.WhenAll(first, queuedBatch, waitingBatch).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(5, handledBatches.Sum(batch => batch.Length));
        Assert.Equal(Enumerable.Range(0, 5), handledBatches.SelectMany(batch => batch).Order());
    }

    [Fact]
    public async Task ExecuteAsync_HandlerFails_ReleasesExecutionSlotForQueuedRequest()
    {
        var expected = new TestBatchException();
        var firstRequestStarted = NewSource();
        var releaseFirstRequest = NewSource();
        var secondRequestHandled = NewSource();

        await using var provider = CreateProvider<int>(
            async (requests, _) =>
            {
                var request = Assert.Single(requests);
                if (request == 1)
                {
                    firstRequestStarted.SetResult();
                    await releaseFirstRequest.Task;
                    throw expected;
                }

                Assert.Equal(2, request);
                secondRequestHandled.SetResult();
            },
            options =>
            {
                options.BatchSize = 1;
                options.MaxPendingRequests = 1;
            });
        var batcher = provider.GetRequiredService<IRequestBatcher<int>>();

        var first = batcher.ExecuteAsync(1);
        await firstRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = batcher.ExecuteAsync(2);

        Assert.False(second.IsCompleted);
        releaseFirstRequest.SetResult();

        var actual = await Assert.ThrowsAsync<TestBatchException>(() => first);
        Assert.Same(expected, actual);
        await second.WaitAsync(TimeSpan.FromSeconds(5));
        await secondRequestHandled.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ExecuteAsync_WaitMode_ProcessesWeightedSubmissions()
    {
        var firstRequestStarted = NewSource();
        var releaseFirstRequest = NewSource();
        var handledRequests = new ConcurrentQueue<int>();

        await using var provider = CreateProvider<int>(
            async (requests, _) =>
            {
                foreach (var request in requests)
                {
                    handledRequests.Enqueue(request);
                    if (request == 0)
                    {
                        firstRequestStarted.SetResult();
                        await releaseFirstRequest.Task;
                    }
                }
            },
            options =>
            {
                options.BatchSize = 3;
                options.MaxPendingRequests = 3;
            });
        var batcher = provider.GetRequiredService<IRequestBatcher<int>>();

        var first = batcher.ExecuteAsync(0);
        await firstRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var explicitBatch = batcher.ExecuteAsync([1, 2, 3]);
        var laterSingle = batcher.ExecuteAsync(4);

        releaseFirstRequest.SetResult();
        await Task.WhenAll(first, explicitBatch, laterSingle).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(Enumerable.Range(0, 5), handledRequests.Order());
    }

    [Fact]
    public async Task ExecuteAsync_CanceledWhileWaiting_ReleasesQueueCapacity()
    {
        var firstRequestStarted = NewSource();
        var releaseFirstRequest = NewSource();
        var handledRequests = new ConcurrentQueue<int>();

        await using var provider = CreateProvider<int>(
            async (requests, _) =>
            {
                var request = Assert.Single(requests);
                handledRequests.Enqueue(request);
                if (request == 0)
                {
                    firstRequestStarted.SetResult();
                    await releaseFirstRequest.Task;
                }
            },
            options =>
            {
                options.BatchSize = 1;
                options.MaxPendingRequests = 2;
            });
        var batcher = provider.GetRequiredService<IRequestBatcher<int>>();

        var first = batcher.ExecuteAsync(0);
        await firstRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var queuedBatch = batcher.ExecuteAsync([1, 2]);

        using var cancellation = new CancellationTokenSource();
        var canceledBatch = batcher.ExecuteAsync([3, 4], cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledBatch);

        var laterBatch = batcher.ExecuteAsync([5, 6]);
        try
        {
            Assert.False(laterBatch.IsCompleted);
        }
        finally
        {
            releaseFirstRequest.TrySetResult();
        }

        await Task.WhenAll(first, queuedBatch, laterBatch).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { 0, 1, 2, 5, 6 }, handledRequests.Order());
    }

    [Fact]
    public async Task ExecuteAsync_PartitionedRequests_RoutesEachRequestByKey()
    {
        const int concurrency = 2;
        var bothPartitionsStarted = NewSource();
        var releasePartitions = NewSource();
        var activeHandlers = 0;
        var maximumActiveHandlers = 0;

        await using var provider = CreateProvider<KeyedRequest>(
            async (_, _) =>
            {
                var active = Interlocked.Increment(ref activeHandlers);
                UpdateMaximum(ref maximumActiveHandlers, active);
                if (active == concurrency)
                {
                    bothPartitionsStarted.TrySetResult();
                }

                await releasePartitions.Task;
                Interlocked.Decrement(ref activeHandlers);
            },
            options =>
            {
                options.BatchSize = 1;
                options.MaxConcurrency = concurrency;
                options.UsePartitionKey(request => request.Key, partitionCount: concurrency);
            });
        var batcher = provider.GetRequiredService<IRequestBatcher<KeyedRequest>>();

        var processing = batcher.ExecuteAsync(
            [new KeyedRequest(1), new KeyedRequest(2)]);

        try
        {
            await bothPartitionsStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(concurrency, Volatile.Read(ref maximumActiveHandlers));
        }
        finally
        {
            releasePartitions.TrySetResult();
        }

        await processing.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ExecuteAsync_PartitionedSubmission_WithSingleConcurrency_StillUsesEachKeyPartition()
    {
        var handledBatches = new ConcurrentQueue<KeyedRequest[]>();

        await using var provider = CreateProvider<KeyedRequest>(
            (requests, _) =>
            {
                handledBatches.Enqueue(requests.ToArray());
                return ValueTask.CompletedTask;
            },
            options =>
            {
                options.BatchSize = 10;
                options.MaxConcurrency = 1;
                options.UsePartitionKey(request => request.Key, partitionCount: 2);
            });
        var batcher = provider.GetRequiredService<IRequestBatcher<KeyedRequest>>();

        await batcher.ExecuteAsync(
                [new KeyedRequest(1, 1), new KeyedRequest(2, 2)])
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, handledBatches.Count);
        Assert.All(handledBatches, batch => Assert.Single(batch));
        Assert.Equal(
            new[] { 1, 2 },
            handledBatches.SelectMany(batch => batch).Select(request => request.Key).Order());
    }

    [Fact]
    public async Task ExecuteAsync_WaitModeOversizedPartitionedBatch_ProcessesAllRoutedItems()
    {
        var handledRequests = new ConcurrentQueue<KeyedRequest>();

        await using var provider = CreateProvider<KeyedRequest>(
            (requests, _) =>
            {
                handledRequests.Enqueue(Assert.Single(requests));
                return ValueTask.CompletedTask;
            },
            options =>
            {
                options.BatchSize = 1;
                options.MaxConcurrency = 2;
                options.MaxPendingRequests = 2;
                options.UsePartitionKey(request => request.Key, partitionCount: 2);
            });
        var batcher = provider.GetRequiredService<IRequestBatcher<KeyedRequest>>();
        KeyedRequest[] requests =
        [
            new(1, 1),
            new(2, 1),
            new(1, 2),
            new(2, 2),
            new(1, 3),
        ];

        await batcher.ExecuteAsync(requests).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(
            requests.OrderBy(request => request.Key).ThenBy(request => request.Sequence),
            handledRequests.OrderBy(request => request.Key).ThenBy(request => request.Sequence));
    }

    [Fact]
    public async Task ExecuteAsync_PartitionedBatchFails_WaitsForOtherHandlerBeforeFaulting()
    {
        var expected = new TestBatchException();
        var secondPartitionStarted = NewSource();
        var releaseSecondPartition = NewSource();

        await using var provider = CreateProvider<KeyedRequest>(
            async (requests, _) =>
            {
                var request = Assert.Single(requests);
                if (request.Key == 1)
                {
                    throw expected;
                }

                secondPartitionStarted.SetResult();
                await releaseSecondPartition.Task;
            },
            options =>
            {
                options.BatchSize = 1;
                options.MaxConcurrency = 2;
                options.UsePartitionKey(request => request.Key, partitionCount: 2);
            });
        var batcher = provider.GetRequiredService<IRequestBatcher<KeyedRequest>>();

        var processing = batcher.ExecuteAsync(
            [new KeyedRequest(1), new KeyedRequest(2)]);
        await secondPartitionStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(processing.IsCompleted);
        releaseSecondPartition.SetResult();

        var actual = await Assert.ThrowsAsync<TestBatchException>(
            () => processing.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Same(expected, actual);
    }

    [Fact]
    public async Task ExecuteAsync_PartitionedBatchHasMultipleFailures_PreservesDistinctExceptions()
    {
        var first = new TestBatchException();
        var second = new TestBatchException();
        var bothHandlersStarted = NewSource();
        var releaseHandlers = NewSource();
        var startedCount = 0;

        await using var provider = CreateProvider<KeyedRequest>(
            async (requests, _) =>
            {
                var request = Assert.Single(requests);
                if (Interlocked.Increment(ref startedCount) == 2)
                {
                    bothHandlersStarted.SetResult();
                }

                await releaseHandlers.Task;
                throw request.Key == 1 ? first : second;
            },
            options =>
            {
                options.BatchSize = 1;
                options.MaxConcurrency = 2;
                options.UsePartitionKey(request => request.Key, partitionCount: 2);
            });
        var batcher = provider.GetRequiredService<IRequestBatcher<KeyedRequest>>();

        var processing = batcher.ExecuteAsync(
            [new KeyedRequest(1), new KeyedRequest(2)]);
        await bothHandlersStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        releaseHandlers.SetResult();

        await Assert.ThrowsAsync<TestBatchException>(
            () => processing.WaitAsync(TimeSpan.FromSeconds(5)));

        var exceptions = processing.Exception!.InnerExceptions;
        Assert.Equal(2, exceptions.Count);
        Assert.Contains(first, exceptions);
        Assert.Contains(second, exceptions);
    }

    [Fact]
    public async Task ExecuteAsync_BatchSplitByBatchSize_WaitsForLastHandlerCall()
    {
        var lastHandlerStarted = NewSource();
        var releaseLastHandler = NewSource();

        await using var provider = CreateProvider<int>(
            async (requests, _) =>
            {
                var request = Assert.Single(requests);
                if (request == 2)
                {
                    lastHandlerStarted.SetResult();
                    await releaseLastHandler.Task;
                }
            },
            options => options.BatchSize = 1);
        var batcher = provider.GetRequiredService<IRequestBatcher<int>>();

        var processing = batcher.ExecuteAsync([1, 2]);
        await lastHandlerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(processing.IsCompleted);
        releaseLastHandler.SetResult();

        await processing.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ExecuteAsync_BatchPartitionSelectorFails_RejectsWithoutPartialHandling()
    {
        var expected = new TestBatchException();
        var handledRequests = new ConcurrentQueue<int>();

        await using var provider = CreateProvider<int>(
            (requests, _) =>
            {
                foreach (var request in requests)
                {
                    handledRequests.Enqueue(request);
                }

                return ValueTask.CompletedTask;
            },
            options =>
            {
                options.MaxConcurrency = 2;
                options.MaxPendingRequests = 3;
                options.UsePartitionKey(
                    request => request >= 0 ? request : throw expected,
                    partitionCount: 2);
            });
        var batcher = provider.GetRequiredService<IRequestBatcher<int>>();

        var actual = await Assert.ThrowsAsync<TestBatchException>(
            () => batcher.ExecuteAsync([1, -1, 2]));

        Assert.Same(expected, actual);
        Assert.Empty(handledRequests);

        await batcher.ExecuteAsync([1, 2]).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { 1, 2 }, handledRequests.Order());
    }

    [Fact]
    public async Task ExecuteAsync_BatchCanceledAfterDispatch_HandlerFailureTakesPrecedence()
    {
        var expected = new TestBatchException();
        var firstRequestStarted = NewSource();
        var releaseFirstRequest = NewSource();
        var handledRequests = new ConcurrentQueue<int>();

        await using var provider = CreateProvider<int>(
            async (requests, _) =>
            {
                var request = Assert.Single(requests);
                handledRequests.Enqueue(request);
                firstRequestStarted.SetResult();
                await releaseFirstRequest.Task;
                throw expected;
            },
            options => options.BatchSize = 1);
        var batcher = provider.GetRequiredService<IRequestBatcher<int>>();
        using var cancellation = new CancellationTokenSource();

        var processing = batcher.ExecuteAsync([1, 2], cancellation.Token);
        await firstRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        Assert.False(processing.IsCompleted);
        releaseFirstRequest.SetResult();

        var actual = await Assert.ThrowsAsync<TestBatchException>(
            () => processing.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Same(expected, actual);
        Assert.Equal(new[] { 1 }, handledRequests);
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

    private sealed record KeyedRequest(int Key, int Sequence = 0);
}
