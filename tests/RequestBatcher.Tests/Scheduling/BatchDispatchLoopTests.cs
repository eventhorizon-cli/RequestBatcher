using System.Runtime.CompilerServices;
using BufferQueue;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RequestBatcher.PendingRequests;
using RequestBatcher.Scheduling;

namespace RequestBatcher.Tests.Scheduling;

public sealed class BatchDispatchLoopTests
{
    [Fact]
    public async Task RunAsync_AllExecutionSlotsOccupied_DoesNotPullNextBatch()
    {
        var firstHandlerStarted = NewSource();
        var releaseFirstHandler = NewSource();
        var secondBatchPullRequested = NewSource();
        var firstRequest = new PendingBatchRequest<int>(1, default, _ => { });
        var secondRequest = new PendingBatchRequest<int>(2, default, _ => { });
        var consumer = CreateConsumer(cancellationToken =>
            YieldTwoBatches(
                [firstRequest],
                [secondRequest],
                secondBatchPullRequested,
                cancellationToken));
        using var cancellation = new CancellationTokenSource();
        using var subject = new BatchDispatchLoop<int>(
            async (requests, _) =>
            {
                if (Assert.Single(requests) == 1)
                {
                    firstHandlerStarted.SetResult();
                    await releaseFirstHandler.Task;
                }
            },
            1,
            NullLogger.Instance,
            typeof(int).FullName!);

        var running = subject.RunAsync(consumer.Object, cancellation.Token);
        await firstHandlerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(secondBatchPullRequested.Task.IsCompleted);

        releaseFirstHandler.SetResult();
        await secondBatchPullRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await secondRequest.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        cancellation.Cancel();
        await running.WaitAsync(TimeSpan.FromSeconds(5));
        consumer.VerifyAll();
    }

    [Fact]
    public async Task RunAsync_OneSlowBatch_RemainingSlotDispatchesNextBatch()
    {
        var firstHandlerStarted = NewSource();
        var secondHandlerStarted = NewSource();
        var thirdHandlerStarted = NewSource();
        var releaseFirstHandler = NewSource();
        var releaseSecondHandler = NewSource();
        var thirdBatchPullRequested = NewSource();
        var firstRequest = new PendingBatchRequest<int>(1, default, _ => { });
        var secondRequest = new PendingBatchRequest<int>(2, default, _ => { });
        var thirdRequest = new PendingBatchRequest<int>(3, default, _ => { });
        var consumer = CreateConsumer(cancellationToken =>
            YieldThreeBatches(
                [firstRequest],
                [secondRequest],
                [thirdRequest],
                thirdBatchPullRequested,
                cancellationToken));
        using var cancellation = new CancellationTokenSource();
        using var subject = new BatchDispatchLoop<int>(
            async (requests, _) =>
            {
                switch (Assert.Single(requests))
                {
                    case 1:
                        firstHandlerStarted.SetResult();
                        await releaseFirstHandler.Task;
                        break;
                    case 2:
                        secondHandlerStarted.SetResult();
                        await releaseSecondHandler.Task;
                        break;
                    case 3:
                        thirdHandlerStarted.SetResult();
                        break;
                }
            },
            2,
            NullLogger.Instance,
            typeof(int).FullName!);

        var running = subject.RunAsync(consumer.Object, cancellation.Token);
        await firstHandlerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await secondHandlerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(thirdBatchPullRequested.Task.IsCompleted);

        releaseSecondHandler.SetResult();
        await thirdBatchPullRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await thirdHandlerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(firstRequest.Completion.IsCompleted);

        releaseFirstHandler.SetResult();
        await firstRequest.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        await secondRequest.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        await thirdRequest.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        cancellation.Cancel();
        await running.WaitAsync(TimeSpan.FromSeconds(5));
        consumer.VerifyAll();
    }

    private static Mock<IBufferPullConsumer<PendingBatchRequest<int>>> CreateConsumer(
        Func<CancellationToken, IAsyncEnumerable<IEnumerable<PendingBatchRequest<int>>>> consume)
    {
        var consumer = new Mock<IBufferPullConsumer<PendingBatchRequest<int>>>(MockBehavior.Strict);
        consumer
            .Setup(candidate => candidate.ConsumeAsync(It.IsAny<CancellationToken>()))
            .Returns(consume);
        return consumer;
    }

    private static async IAsyncEnumerable<IEnumerable<PendingBatchRequest<int>>> YieldTwoBatches(
        PendingBatchRequest<int>[] firstBatch,
        PendingBatchRequest<int>[] secondBatch,
        TaskCompletionSource secondBatchPullRequested,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return firstBatch;
        secondBatchPullRequested.SetResult();
        yield return secondBatch;
        await Task.Delay(Timeout.Infinite, cancellationToken);
    }

    private static async IAsyncEnumerable<IEnumerable<PendingBatchRequest<int>>> YieldThreeBatches(
        PendingBatchRequest<int>[] firstBatch,
        PendingBatchRequest<int>[] secondBatch,
        PendingBatchRequest<int>[] thirdBatch,
        TaskCompletionSource thirdBatchPullRequested,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return firstBatch;
        yield return secondBatch;
        thirdBatchPullRequested.SetResult();
        yield return thirdBatch;
        await Task.Delay(Timeout.Infinite, cancellationToken);
    }

    private static TaskCompletionSource NewSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
