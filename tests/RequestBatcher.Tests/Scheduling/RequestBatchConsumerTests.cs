using Microsoft.Extensions.Logging.Abstractions;
using RequestBatcher.PendingRequests;
using RequestBatcher.Scheduling;

namespace RequestBatcher.Tests.Scheduling;

public sealed class RequestBatchConsumerTests
{
    [Fact]
    public async Task ExecuteAsync_HandlerIsRunning_CompletesRequestsAfterHandler()
    {
        var handlerStarted = NewSource();
        var releaseHandler = NewSource();
        var pendingRequest = new PendingBatchRequest<int>(42, default, _ => { });
        var subject = new RequestBatchConsumer<int>(
            async (requests, _) =>
            {
                Assert.Equal(new[] { 42 }, requests);
                handlerStarted.SetResult();
                await releaseHandler.Task;
            },
            NullLogger.Instance,
            typeof(int).FullName!);

        var processing = subject.ExecuteAsync([pendingRequest], CancellationToken.None).AsTask();
        await handlerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(pendingRequest.Completion.IsCompleted);
        releaseHandler.SetResult();

        await processing.WaitAsync(TimeSpan.FromSeconds(5));
        await pendingRequest.Completion.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ExecuteAsync_HandlerFails_CompletesRequestWithOriginalException()
    {
        var expected = new TestHandlerException();
        var handlerInvocationCount = 0;
        var pendingRequest = new PendingBatchRequest<int>(42, default, _ => { });
        var subject = new RequestBatchConsumer<int>(
            (_, _) =>
            {
                Interlocked.Increment(ref handlerInvocationCount);
                return ValueTask.FromException(expected);
            },
            NullLogger.Instance,
            typeof(int).FullName!);

        await subject.ExecuteAsync([pendingRequest], CancellationToken.None).AsTask();

        var actual = await Assert.ThrowsAsync<TestHandlerException>(
            () => pendingRequest.Completion.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Same(expected, actual);
        Assert.Equal(1, Volatile.Read(ref handlerInvocationCount));
    }

    [Fact]
    public async Task ExecuteAsync_RequestCanceledBeforeDispatch_SkipsHandler()
    {
        using var cancellation = new CancellationTokenSource();
        var handlerInvocationCount = 0;
        var pendingRequest = new PendingBatchRequest<int>(
            42,
            cancellation.Token,
            _ => { });
        var subject = new RequestBatchConsumer<int>(
            (_, _) =>
            {
                Interlocked.Increment(ref handlerInvocationCount);
                return ValueTask.CompletedTask;
            },
            NullLogger.Instance,
            typeof(int).FullName!);

        cancellation.Cancel();

        await subject.ExecuteAsync([pendingRequest], CancellationToken.None).AsTask();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => pendingRequest.Completion.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, Volatile.Read(ref handlerInvocationCount));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ExecuteAsync_MixedCanceledAndSuccessfulRequests_CompletesEachRequestOnce(
        int canceledIndex)
    {
        using var cancellation = new CancellationTokenSource();
        var finishedCounts = new int[3];
        IReadOnlyList<int> handledRequests = Array.Empty<int>();
        var requests = Enumerable.Range(1, 3)
            .Select((request, index) => CreatePendingRequest(
                request,
                index == canceledIndex ? cancellation.Token : CancellationToken.None,
                finishedCounts))
            .ToArray();
        var subject = new RequestBatchConsumer<int>(
            (batchRequests, _) =>
            {
                handledRequests = batchRequests;
                return ValueTask.CompletedTask;
            },
            NullLogger.Instance,
            typeof(int).FullName!);

        cancellation.Cancel();
        await subject.ExecuteAsync(requests, CancellationToken.None).AsTask();

        Assert.Equal(
            Enumerable.Range(1, 3).Where((_, index) => index != canceledIndex),
            handledRequests);
        foreach (var request in requests.Where((_, index) => index != canceledIndex))
        {
            Assert.True(request.Completion.IsCompletedSuccessfully);
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => requests[canceledIndex].Completion.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(new[] { 1, 1, 1 }, finishedCounts);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ExecuteAsync_MixedCanceledAndFailedRequests_FansOutFailureToActiveRequests(
        int canceledIndex)
    {
        using var cancellation = new CancellationTokenSource();
        var expected = new TestHandlerException();
        var finishedCounts = new int[3];
        IReadOnlyList<int> handledRequests = Array.Empty<int>();
        var requests = Enumerable.Range(1, 3)
            .Select((request, index) => CreatePendingRequest(
                request,
                index == canceledIndex ? cancellation.Token : CancellationToken.None,
                finishedCounts))
            .ToArray();
        var subject = new RequestBatchConsumer<int>(
            (batchRequests, _) =>
            {
                handledRequests = batchRequests;
                return ValueTask.FromException(expected);
            },
            NullLogger.Instance,
            typeof(int).FullName!);

        cancellation.Cancel();
        await subject.ExecuteAsync(requests, CancellationToken.None).AsTask();

        Assert.Equal(
            Enumerable.Range(1, 3).Where((_, index) => index != canceledIndex),
            handledRequests);
        foreach (var request in requests.Where((_, index) => index != canceledIndex))
        {
            Assert.Same(expected, await Assert.ThrowsAsync<TestHandlerException>(() => request.Completion));
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => requests[canceledIndex].Completion.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(new[] { 1, 1, 1 }, finishedCounts);
    }

    [Fact]
    public async Task ExecuteAsync_OverlappingBatchesCompletedInReverse_KeepResultsAndRequestArraysIsolated()
    {
        var firstStarted = NewSource();
        var secondStarted = NewSource();
        var releaseFirst = NewSource();
        var releaseSecond = NewSource();
        var firstException = new TestHandlerException();
        var finishedCounts = new int[6];
        var receivedBatches = new System.Collections.Concurrent.ConcurrentQueue<IReadOnlyList<int>>();
        var firstRequests = new[]
        {
            CreatePendingRequest(1, CancellationToken.None, finishedCounts),
            CreatePendingRequest(2, CancellationToken.None, finishedCounts)
        };
        var secondRequests = new[]
        {
            CreatePendingRequest(3, CancellationToken.None, finishedCounts),
            CreatePendingRequest(4, CancellationToken.None, finishedCounts)
        };
        var subject = new RequestBatchConsumer<int>(
            async (requests, _) =>
            {
                receivedBatches.Enqueue(requests);
                if (requests[0] == 1)
                {
                    firstStarted.SetResult();
                    await releaseFirst.Task;
                    throw firstException;
                }

                if (requests[0] == 5)
                {
                    return;
                }

                secondStarted.SetResult();
                await releaseSecond.Task;
            },
            NullLogger.Instance,
            typeof(int).FullName!);

        var firstProcessing = subject.ExecuteAsync(firstRequests, CancellationToken.None).AsTask();
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var secondProcessing = subject.ExecuteAsync(secondRequests, CancellationToken.None).AsTask();
        await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        releaseSecond.SetResult();
        await secondProcessing.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(firstProcessing.IsCompleted);
        Assert.False(firstRequests[0].Completion.IsCompleted);
        Assert.False(firstRequests[1].Completion.IsCompleted);

        releaseFirst.SetResult();
        await firstProcessing.WaitAsync(TimeSpan.FromSeconds(5));

        var thirdRequests = new[]
        {
            CreatePendingRequest(5, CancellationToken.None, finishedCounts),
            CreatePendingRequest(6, CancellationToken.None, finishedCounts)
        };
        await subject.ExecuteAsync(thirdRequests, CancellationToken.None).AsTask();

        var receivedBatchArrays = receivedBatches.ToArray();
        Assert.Equal(3, receivedBatchArrays.Length);
        Assert.Equal(new[] { 1, 2 }, receivedBatchArrays[0]);
        Assert.Equal(new[] { 3, 4 }, receivedBatchArrays[1]);
        Assert.Equal(new[] { 5, 6 }, receivedBatchArrays[2]);
        Assert.Same(firstException, await Assert.ThrowsAsync<TestHandlerException>(
            () => firstRequests[0].Completion));
        Assert.Same(firstException, await Assert.ThrowsAsync<TestHandlerException>(
            () => firstRequests[1].Completion));
        Assert.True(secondRequests[0].Completion.IsCompletedSuccessfully);
        Assert.True(secondRequests[1].Completion.IsCompletedSuccessfully);
        Assert.True(thirdRequests[0].Completion.IsCompletedSuccessfully);
        Assert.True(thirdRequests[1].Completion.IsCompletedSuccessfully);
        Assert.Equal(new[] { 1, 1, 1, 1, 1, 1 }, finishedCounts);
    }

    private static PendingBatchRequest<int> CreatePendingRequest(
        int request,
        CancellationToken cancellationToken,
        int[] finishedCounts) =>
        new(
            request,
            cancellationToken,
            pendingRequest => Interlocked.Increment(ref finishedCounts[pendingRequest.Request - 1]));

    private static TaskCompletionSource NewSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class TestHandlerException : Exception;
}
