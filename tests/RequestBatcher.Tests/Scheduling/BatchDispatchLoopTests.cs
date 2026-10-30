using System.Runtime.CompilerServices;
using BufferQueue;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RequestBatcher.Diagnostics;
using RequestBatcher.PendingRequests;
using RequestBatcher.Scheduling;

namespace RequestBatcher.Tests.Scheduling;

public sealed class BatchDispatchLoopTests : IDisposable
{
    private readonly RequestBatcherMetrics _metrics = new(128);
    public void Dispose() => _metrics.Dispose();

    [Fact]
    public async Task RunAsync_ExhaustedConsumer_ReleasesSlotAndDisposesEnumerator()
    {
        var firstSequence = CreateEmptySequence();
        var secondSequence = CreateEmptySequence();
        var consumeCount = 0;
        var consumer = CreateConsumer(_ =>
            Interlocked.Increment(ref consumeCount) == 1
                ? firstSequence.Sequence.Object
                : secondSequence.Sequence.Object);
        using var subject = new BatchDispatchLoop<int>(
            (_, _) => ValueTask.CompletedTask,
            1,
            NullLogger.Instance,
            typeof(int).FullName!,
            _metrics);

        await subject.RunAsync(consumer.Object, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));
        firstSequence.Sequence.Verify(
            candidate => candidate.GetAsyncEnumerator(It.IsAny<CancellationToken>()),
            Times.Once);
        firstSequence.Enumerator.Verify(candidate => candidate.MoveNextAsync(), Times.Once);
        firstSequence.Enumerator.Verify(candidate => candidate.DisposeAsync(), Times.Once);

        await subject.RunAsync(consumer.Object, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));
        secondSequence.Sequence.Verify(
            candidate => candidate.GetAsyncEnumerator(It.IsAny<CancellationToken>()),
            Times.Once);
        secondSequence.Enumerator.Verify(candidate => candidate.MoveNextAsync(), Times.Once);
        secondSequence.Enumerator.Verify(candidate => candidate.DisposeAsync(), Times.Once);

        consumer.Verify(
            candidate => candidate.ConsumeAsync(It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    [Fact]
    public async Task RunAsync_ExhaustedConsumer_DrainsAfterBlockedHandlerCompletes()
    {
        var handlerStarted = NewSource();
        var releaseHandler = NewSource();
        var finishCount = 0;
        var request = new PendingBatchRequest<int>(
            1,
            default,
            _ => Interlocked.Increment(ref finishCount));
        var sequence = CreateSingleItemSequence(request);
        var consumer = CreateConsumer(_ => sequence.Sequence.Object);
        using var subject = new BatchDispatchLoop<int>(
            async (requests, _) =>
            {
                Assert.Equal(1, Assert.Single(requests));
                handlerStarted.SetResult();
                await releaseHandler.Task;
            },
            2,
            NullLogger.Instance,
            typeof(int).FullName!,
            _metrics);

        var running = subject.RunAsync(consumer.Object, CancellationToken.None);
        await handlerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var draining = subject.DrainAsync();

        await running.WaitAsync(TimeSpan.FromSeconds(5));
        sequence.Sequence.Verify(
            candidate => candidate.GetAsyncEnumerator(It.IsAny<CancellationToken>()),
            Times.Once);
        sequence.Enumerator.Verify(candidate => candidate.MoveNextAsync(), Times.Exactly(2));
        sequence.Enumerator.VerifyGet(candidate => candidate.Current, Times.Once);
        sequence.Enumerator.Verify(candidate => candidate.DisposeAsync(), Times.Once);
        Assert.False(request.Completion.IsCompleted);
        Assert.False(draining.IsCompleted);

        releaseHandler.SetResult();
        await request.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        await draining.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, Volatile.Read(ref finishCount));
        consumer.Verify(
            candidate => candidate.ConsumeAsync(It.IsAny<CancellationToken>()),
            Times.Once);
    }

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
            typeof(int).FullName!,
            _metrics);

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
            typeof(int).FullName!,
            _metrics);

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
        Func<CancellationToken, IAsyncEnumerable<IReadOnlyList<PendingBatchRequest<int>>>> consume)
    {
        var consumer = new Mock<IBufferPullConsumer<PendingBatchRequest<int>>>(MockBehavior.Strict);
        consumer
            .Setup(candidate => candidate.ConsumeAsync(It.IsAny<CancellationToken>()))
            .Returns(consume);
        return consumer;
    }

    private static async IAsyncEnumerable<IReadOnlyList<PendingBatchRequest<int>>> YieldTwoBatches(
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

    private static ConsumerSequence CreateEmptySequence()
    {
        var enumerator = new Mock<IAsyncEnumerator<IReadOnlyList<PendingBatchRequest<int>>>>(MockBehavior.Strict);
        enumerator
            .Setup(candidate => candidate.MoveNextAsync())
            .Returns(new ValueTask<bool>(false));
        enumerator
            .Setup(candidate => candidate.DisposeAsync())
            .Returns(ValueTask.CompletedTask);

        return CreateSequence(enumerator);
    }

    private static ConsumerSequence CreateSingleItemSequence(PendingBatchRequest<int> request)
    {
        var moveNextCount = 0;
        var enumerator = new Mock<IAsyncEnumerator<IReadOnlyList<PendingBatchRequest<int>>>>(MockBehavior.Strict);
        enumerator
            .SetupGet(candidate => candidate.Current)
            .Returns([request]);
        enumerator
            .Setup(candidate => candidate.MoveNextAsync())
            .Returns(() => new ValueTask<bool>(Interlocked.Increment(ref moveNextCount) == 1));
        enumerator
            .Setup(candidate => candidate.DisposeAsync())
            .Returns(ValueTask.CompletedTask);

        return CreateSequence(enumerator);
    }

    private static ConsumerSequence CreateSequence(
        Mock<IAsyncEnumerator<IReadOnlyList<PendingBatchRequest<int>>>> enumerator)
    {
        var sequence = new Mock<IAsyncEnumerable<IReadOnlyList<PendingBatchRequest<int>>>>(MockBehavior.Strict);
        sequence
            .Setup(candidate => candidate.GetAsyncEnumerator(It.IsAny<CancellationToken>()))
            .Returns(enumerator.Object);
        return new ConsumerSequence(sequence, enumerator);
    }

    private static async IAsyncEnumerable<IReadOnlyList<PendingBatchRequest<int>>> YieldThreeBatches(
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

    private sealed class ConsumerSequence(
        Mock<IAsyncEnumerable<IReadOnlyList<PendingBatchRequest<int>>>> sequence,
        Mock<IAsyncEnumerator<IReadOnlyList<PendingBatchRequest<int>>>> enumerator)
    {
        public Mock<IAsyncEnumerable<IReadOnlyList<PendingBatchRequest<int>>>> Sequence { get; } = sequence;

        public Mock<IAsyncEnumerator<IReadOnlyList<PendingBatchRequest<int>>>> Enumerator { get; } = enumerator;
    }
}
