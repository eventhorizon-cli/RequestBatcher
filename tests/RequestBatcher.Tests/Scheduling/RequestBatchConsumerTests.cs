using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RequestBatcher.Diagnostics;
using RequestBatcher.PendingRequests;
using RequestBatcher.Scheduling;

namespace RequestBatcher.Tests.Scheduling;

public sealed class RequestBatchConsumerTests : IDisposable
{
    private readonly RequestBatcherMetrics _metrics = new(128);
    public void Dispose() => _metrics.Dispose();

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
            typeof(int).FullName!,
            _metrics);

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
            typeof(int).FullName!,
            _metrics);

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
            typeof(int).FullName!,
            _metrics);

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
            typeof(int).FullName!,
            _metrics);

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
            typeof(int).FullName!,
            _metrics);

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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_EnumerationFailsAfterStartedRequests_FaultsOnlyStartedRequests(
        bool includeCanceledRequest)
    {
        using var cancellation = new CancellationTokenSource();
        var expected = new EnumerationTestException();
        var finishedCounts = new int[3];
        var first = CreatePendingRequest(1, CancellationToken.None, finishedCounts);
        var unvisited = CreatePendingRequest(3, CancellationToken.None, finishedCounts);
        PendingBatchRequest<int>? canceled = null;
        PendingBatchRequest<int>[] bufferedRequests;
        if (includeCanceledRequest)
        {
            var canceledRequest = CreatePendingRequest(2, cancellation.Token, finishedCounts);
            canceled = canceledRequest;
            bufferedRequests = [first, canceledRequest, unvisited];
        }
        else
        {
            bufferedRequests = [first, unvisited];
        }

        var batch = CreateThrowingBatch(
            bufferedRequests,
            includeCanceledRequest ? 2 : 1,
            expected);
        var handlerInvocationCount = 0;
        var subject = new RequestBatchConsumer<int>(
            (_, _) =>
            {
                Interlocked.Increment(ref handlerInvocationCount);
                return ValueTask.CompletedTask;
            },
            NullLogger.Instance,
            typeof(int).FullName!,
            _metrics);

        if (includeCanceledRequest)
        {
            cancellation.Cancel();
        }

        var actual = await Assert.ThrowsAsync<EnumerationTestException>(
            () => subject.ExecuteAsync(batch.Object, CancellationToken.None).AsTask());

        Assert.Same(expected, actual);
        Assert.Equal(0, Volatile.Read(ref handlerInvocationCount));
        Assert.Same(expected, await Assert.ThrowsAsync<EnumerationTestException>(
            () => first.Completion));
        Assert.Equal(1, Volatile.Read(ref finishedCounts[0]));

        if (includeCanceledRequest)
        {
            Assert.True(canceled!.Completion.IsCanceled);
            Assert.Equal(1, Volatile.Read(ref finishedCounts[1]));
        }

        Assert.False(unvisited.Completion.IsCompleted);
        Assert.Equal(0, Volatile.Read(ref finishedCounts[2]));

        Assert.True(unvisited.TryStartProcessing());
        unvisited.CompleteSuccessfully();
        Assert.True(unvisited.Completion.IsCompletedSuccessfully);
        Assert.Equal(1, Volatile.Read(ref finishedCounts[2]));
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
            typeof(int).FullName!,
            _metrics);

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

    private static Mock<IReadOnlyList<PendingBatchRequest<int>>> CreateThrowingBatch(
        IReadOnlyList<PendingBatchRequest<int>> requests,
        int throwAtIndex,
        Exception exception)
    {
        var batch = new Mock<IReadOnlyList<PendingBatchRequest<int>>>(MockBehavior.Strict);
        batch.Setup(candidate => candidate.Count).Returns(requests.Count);
        batch.Setup(candidate => candidate[It.IsAny<int>()])
            .Returns((int index) => requests[index]);
        batch.Setup(candidate => candidate.GetEnumerator())
            .Returns(() => EnumerateUntilFailure(requests, throwAtIndex, exception).GetEnumerator());
        return batch;
    }

    private static TaskCompletionSource NewSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static IEnumerable<PendingBatchRequest<int>> EnumerateUntilFailure(
        IReadOnlyList<PendingBatchRequest<int>> requests,
        int throwAtIndex,
        Exception exception)
    {
        for (var index = 0; index < requests.Count; index++)
        {
            if (index == throwAtIndex)
            {
                throw exception;
            }

            yield return requests[index];
        }
    }

    private sealed class TestHandlerException : Exception;

    private sealed class EnumerationTestException : Exception;
}
