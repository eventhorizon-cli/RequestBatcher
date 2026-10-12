using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;

namespace RequestBatcher.Tests;

public sealed class ResponseCompletionTests
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task ProcessAsync_ResponseSetBeforeHandlerCompletion_DelaysCallerUntilScopeDisposed()
    {
        var probe = new ResponseHandlerProbe();
        var handlerStarted = NewSource();
        var releaseHandler = NewSource();

        await using var provider = CreateScopedProvider(
            probe,
            async (items, _) =>
            {
                items[0].SetResponse("answer");
                handlerStarted.SetResult();
                await releaseHandler.Task;
            });
        var batcher = provider.GetRequiredService<IRequestBatcher<int, string>>();
        var response = batcher.ProcessAsync(1);

        try
        {
            await handlerStarted.Task.WaitAsync(_timeout);

            Assert.False(response.IsCompleted);
            Assert.Equal(0, probe.DisposeCount);

            releaseHandler.SetResult();
            await probe.DisposalStarted.Task.WaitAsync(_timeout);
            Assert.False(response.IsCompleted);
            Assert.Equal(0, probe.DisposeCount);

            probe.ReleaseDisposal.SetResult();
            Assert.Equal("answer", await response.WaitAsync(_timeout));
            Assert.Equal(1, probe.DisposeCount);
        }
        finally
        {
            releaseHandler.TrySetResult();
            probe.ReleaseDisposal.TrySetResult();
        }
    }

    [Fact]
    public async Task ProcessAsync_ResponseSetBeforeDisposalFailure_PropagatesDisposalException()
    {
        var expected = new TestBatchException();
        var probe = new ResponseHandlerProbe { DisposalException = expected };

        await using var provider = CreateScopedProvider(
            probe,
            (items, _) =>
            {
                items[0].SetResponse("ignored");
                return ValueTask.CompletedTask;
            });
        var batcher = provider.GetRequiredService<IRequestBatcher<int, string>>();
        var response = batcher.ProcessAsync(1);

        try
        {
            await probe.DisposalStarted.Task.WaitAsync(_timeout);
            Assert.False(response.IsCompleted);

            probe.ReleaseDisposal.SetResult();
            var actual = await Assert.ThrowsAsync<TestBatchException>(
                () => response.WaitAsync(_timeout));
            Assert.Same(expected, actual);
        }
        finally
        {
            probe.ReleaseDisposal.TrySetResult();
        }
    }

    [Fact]
    public async Task ProcessAsync_ResponseSetThenHandlerThrows_PropagatesOriginalException()
    {
        var expected = new TestBatchException();

        await using var provider = CreateProvider(
            (items, _) =>
            {
                items[0].SetResponse("ignored");
                return ValueTask.FromException(expected);
            });
        var batcher = provider.GetRequiredService<IRequestBatcher<int, string>>();

        var actual = await Assert.ThrowsAsync<TestBatchException>(
            () => batcher.ProcessAsync(1).WaitAsync(_timeout));

        Assert.Same(expected, actual);
    }

    [Fact]
    public async Task ProcessAsync_MissingResponseInSharedHandlerBatch_FaultsEveryItem()
    {
        var blockerStarted = NewSource();
        var releaseBlocker = NewSource();
        var targetBatchSize = NewSource<int>();

        await using var provider = CreateProvider(
            async (items, _) =>
            {
                if (items[0].Request == 0)
                {
                    items[0].SetResponse("blocker");
                    blockerStarted.SetResult();
                    await releaseBlocker.Task;
                    return;
                }

                targetBatchSize.SetResult(items.Count);
                items[0].SetResponse("first");
            },
            options => options.BatchSize = 2);
        var batcher = provider.GetRequiredService<IRequestBatcher<int, string>>();
        var blocker = batcher.ProcessAsync(0);

        try
        {
            await blockerStarted.Task.WaitAsync(_timeout);
            var first = batcher.ProcessAsync(1);
            var second = batcher.ProcessAsync(2);
            releaseBlocker.SetResult();

            Assert.Equal(2, await targetBatchSize.Task.WaitAsync(_timeout));
            var firstException = await Assert.ThrowsAsync<InvalidOperationException>(
                () => first.WaitAsync(_timeout));
            var secondException = await Assert.ThrowsAsync<InvalidOperationException>(
                () => second.WaitAsync(_timeout));

            Assert.Same(firstException, secondException);
            await blocker.WaitAsync(_timeout);
        }
        finally
        {
            releaseBlocker.TrySetResult();
        }
    }

    [Fact]
    public async Task ProcessAsync_CallerCanceledAfterDispatchSuccess_ReturnsActualResponse()
    {
        var handlerStarted = NewSource();
        var releaseHandler = NewSource();

        await using var provider = CreateProvider(
            async (items, _) =>
            {
                handlerStarted.SetResult();
                await releaseHandler.Task;
                items[0].SetResponse("success");
            });
        var batcher = provider.GetRequiredService<IRequestBatcher<int, string>>();
        using var cancellation = new CancellationTokenSource();
        var response = batcher.ProcessAsync(1, cancellation.Token);

        try
        {
            await handlerStarted.Task.WaitAsync(_timeout);
            cancellation.Cancel();
            releaseHandler.SetResult();

            Assert.Equal("success", await response.WaitAsync(_timeout));
        }
        finally
        {
            releaseHandler.TrySetResult();
        }
    }

    [Fact]
    public async Task ProcessAsync_CallerCanceledAfterDispatchFailure_PropagatesActualFailure()
    {
        var expected = new TestBatchException();
        var handlerStarted = NewSource();
        var releaseHandler = NewSource();

        await using var provider = CreateProvider(
            async (_, _) =>
            {
                handlerStarted.SetResult();
                await releaseHandler.Task;
                throw expected;
            });
        var batcher = provider.GetRequiredService<IRequestBatcher<int, string>>();
        using var cancellation = new CancellationTokenSource();
        var response = batcher.ProcessAsync(1, cancellation.Token);

        try
        {
            await handlerStarted.Task.WaitAsync(_timeout);
            cancellation.Cancel();
            releaseHandler.SetResult();

            var actual = await Assert.ThrowsAsync<TestBatchException>(
                () => response.WaitAsync(_timeout));
            Assert.Same(expected, actual);
        }
        finally
        {
            releaseHandler.TrySetResult();
        }
    }

    [Fact]
    public async Task ProcessAsync_CallerCanceledBeforeDispatch_CancelsResponseAndSkipsHandler()
    {
        var handlerStarted = NewSource();
        var releaseHandler = NewSource();
        var handledRequests = new ConcurrentQueue<int>();

        await using var provider = CreateProvider(
            async (items, _) =>
            {
                handledRequests.Enqueue(items[0].Request);
                if (items[0].Request == 1)
                {
                    handlerStarted.SetResult();
                    await releaseHandler.Task;
                }

                items[0].SetResponse(items[0].Request.ToString());
            },
            options => options.BatchSize = 1);
        var batcher = provider.GetRequiredService<IRequestBatcher<int, string>>();
        var first = batcher.ProcessAsync(1);

        try
        {
            await handlerStarted.Task.WaitAsync(_timeout);

            using var cancellation = new CancellationTokenSource();
            var canceled = batcher.ProcessAsync(2, cancellation.Token);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => canceled.WaitAsync(_timeout));

            releaseHandler.SetResult();
            Assert.Equal("1", await first.WaitAsync(_timeout));
            Assert.Equal(new[] { 1 }, handledRequests);
        }
        finally
        {
            releaseHandler.TrySetResult();
        }
    }

    [Fact]
    public async Task ProcessAsync_CallerCanceledWhileWaitingCapacity_CancelsResponse()
    {
        var handlerStarted = NewSource();
        var releaseHandler = NewSource();

        await using var provider = CreateProvider(
            async (items, _) =>
            {
                if (items[0].Request == 1)
                {
                    handlerStarted.SetResult();
                    await releaseHandler.Task;
                }

                items[0].SetResponse(items[0].Request.ToString());
            },
            options => options.MaxPendingRequests = 1);
        var batcher = provider.GetRequiredService<IRequestBatcher<int, string>>();
        var first = batcher.ProcessAsync(1);

        try
        {
            await handlerStarted.Task.WaitAsync(_timeout);
            var queued = batcher.ProcessAsync(2);
            using var cancellation = new CancellationTokenSource();
            var waiting = batcher.ProcessAsync(3, cancellation.Token);
            Assert.False(waiting.IsCompleted);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => waiting.WaitAsync(_timeout));

            releaseHandler.SetResult();
            await Task.WhenAll(first, queued).WaitAsync(_timeout);
        }
        finally
        {
            releaseHandler.TrySetResult();
        }
    }

    [Fact]
    public async Task ProcessAsync_FailModeCapacityRejected_ReturnsQueueFullException()
    {
        var handlerStarted = NewSource();
        var releaseHandler = NewSource();

        await using var provider = CreateProvider(
            async (items, _) =>
            {
                if (items[0].Request == 1)
                {
                    handlerStarted.SetResult();
                    await releaseHandler.Task;
                }

                items[0].SetResponse(items[0].Request.ToString());
            },
            options =>
            {
                options.MaxPendingRequests = 1;
                options.FullMode = RequestBatchFullMode.Fail;
            });
        var batcher = provider.GetRequiredService<IRequestBatcher<int, string>>();
        var first = batcher.ProcessAsync(1);

        try
        {
            await handlerStarted.Task.WaitAsync(_timeout);
            var queued = batcher.ProcessAsync(2);
            var rejected = batcher.ProcessAsync(3);
            var exception = await Assert.ThrowsAsync<RequestBatchQueueFullException>(
                () => rejected.WaitAsync(_timeout));
            Assert.Equal(1, exception.Capacity);
            Assert.Equal(1, exception.RequestedCount);

            releaseHandler.SetResult();
            await Task.WhenAll(first, queued).WaitAsync(_timeout);
        }
        finally
        {
            releaseHandler.TrySetResult();
        }
    }

    [Fact]
    public async Task ProcessAsync_CompletedResponseTask_AwaitsSameResponse()
    {
        await using var provider = CreateResponseValueProvider();
        var responseBatcher = provider.GetRequiredService<IRequestBatcher<int, ResponseValue>>();
        var response = responseBatcher.ProcessAsync(7);

        var first = await response.WaitAsync(_timeout);
        var second = await response.WaitAsync(_timeout);

        Assert.Same(first, second);
    }

    [Fact]
    public async Task ProcessAsync_HandlerThrowsOperationCanceledException_ReturnsCanceledTask()
    {
        using var handlerCancellation = new CancellationTokenSource();
        var expectedToken = handlerCancellation.Token;

        await using var provider = CreateProvider(
            (_, _) => ValueTask.FromException(new OperationCanceledException(expectedToken)));
        var batcher = provider.GetRequiredService<IRequestBatcher<int, string>>();

        var response = batcher.ProcessAsync(1);

        var actual = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => response.WaitAsync(_timeout));
        Assert.True(response.IsCanceled);
        Assert.Equal(expectedToken, actual.CancellationToken);
        Assert.IsType<TaskCanceledException>(actual);
    }

    [Fact]
    public async Task ProcessAsync_PreCanceledSubmission_ReturnsCanceledResponseTask()
    {
        await using var provider = CreateProvider(
            (_, _) => throw new TestBatchException());
        var batcher = provider.GetRequiredService<IRequestBatcher<int, string>>();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var response = batcher.ProcessAsync(1, cancellation.Token);

        var actual = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => response.WaitAsync(_timeout));
        Assert.True(response.IsCanceled);
        Assert.Equal(cancellation.Token, actual.CancellationToken);
    }

    [Fact]
    public async Task ProcessAsync_DisposedBatcher_ReturnsObjectDisposedTask()
    {
        var provider = CreateProvider(
            (_, _) => throw new TestBatchException());
        var batcher = provider.GetRequiredService<IRequestBatcher<int, string>>();

        try
        {
            await provider.DisposeAsync();

            var response = batcher.ProcessAsync(1);

            await Assert.ThrowsAsync<ObjectDisposedException>(
                () => response.WaitAsync(_timeout));
        }
        finally
        {
            await provider.DisposeAsync();
        }
    }

    private static ServiceProvider CreateProvider(
        Func<IReadOnlyList<RequestBatchItem<int, string>>, CancellationToken, ValueTask> handler,
        Action<RequestBatchOptions<int>>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(handler);
        services.AddRequestBatcher<int, string, DelegateResponseHandler>(ServiceLifetime.Singleton, configure);
        return services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    private static ServiceProvider CreateScopedProvider(
        ResponseHandlerProbe probe,
        Func<IReadOnlyList<RequestBatchItem<int, string>>, CancellationToken, ValueTask> handler)
    {
        var services = new ServiceCollection();
        services.AddSingleton(probe);
        services.AddSingleton(handler);
        services.AddRequestBatcher<int, string, AsyncScopedResponseHandler>(ServiceLifetime.Scoped);
        return services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    private static ServiceProvider CreateResponseValueProvider()
    {
        var services = new ServiceCollection();
        services.AddRequestBatcher<int, ResponseValue, ResponseValueHandler>(ServiceLifetime.Singleton);
        return services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    private static TaskCompletionSource NewSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static TaskCompletionSource<T> NewSource<T>() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class DelegateResponseHandler(
        Func<IReadOnlyList<RequestBatchItem<int, string>>, CancellationToken, ValueTask> handler)
        : IRequestBatchHandler<int, string>
    {
        public ValueTask HandleAsync(
            IReadOnlyList<RequestBatchItem<int, string>> requests,
            CancellationToken cancellationToken = default) =>
            handler(requests, cancellationToken);
    }

    private sealed class AsyncScopedResponseHandler(
        ResponseHandlerProbe probe,
        Func<IReadOnlyList<RequestBatchItem<int, string>>, CancellationToken, ValueTask> handler)
        : IRequestBatchHandler<int, string>, IAsyncDisposable
    {
        public ValueTask HandleAsync(
            IReadOnlyList<RequestBatchItem<int, string>> requests,
            CancellationToken cancellationToken = default) =>
            handler(requests, cancellationToken);

        public async ValueTask DisposeAsync()
        {
            probe.DisposalStarted.TrySetResult();
            await probe.ReleaseDisposal.Task.ConfigureAwait(false);
            if (probe.DisposalException is { } exception)
            {
                throw exception;
            }

            probe.IncrementDisposeCount();
        }
    }

    private sealed class ResponseValueHandler : IRequestBatchHandler<int, ResponseValue>
    {
        public ValueTask HandleAsync(
            IReadOnlyList<RequestBatchItem<int, ResponseValue>> requests,
            CancellationToken cancellationToken = default)
        {
            requests[0].SetResponse(new ResponseValue(requests[0].Request));
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ResponseHandlerProbe
    {
        private int _disposeCount;

        public TaskCompletionSource DisposalStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseDisposal { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Exception? DisposalException { get; init; }

        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public void IncrementDisposeCount() => Interlocked.Increment(ref _disposeCount);
    }

    private sealed record ResponseValue(int Value);

    private sealed class TestBatchException : Exception;
}
