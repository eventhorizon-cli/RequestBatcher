using System.Runtime.CompilerServices;
using BufferQueue;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RequestBatcher.Internal;

namespace RequestBatcher.Tests.Internal;

public sealed class RequestBatchConsumerTests
{
    [Fact]
    public async Task RunAsync_HandlerIsRunning_DoesNotCommitUntilHandlerCompletes()
    {
        var handlerStarted = NewSource();
        var releaseHandler = NewSource();
        var committed = NewSource();
        var pendingRequest = new PendingBatchRequest<int>(42, default, _ => { });
        var consumer = CreateConsumer([pendingRequest], committed);
        var subject = new RequestBatchConsumer<int>(
            async (requests, _) =>
            {
                Assert.Equal(new[] { 42 }, requests);
                handlerStarted.SetResult();
                await releaseHandler.Task;
            },
            10,
            NullLogger.Instance,
            typeof(int).FullName!);
        using var cancellation = new CancellationTokenSource();

        var consuming = subject.RunAsync(consumer.Object, cancellation.Token);
        await handlerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(committed.Task.IsCompleted);
        releaseHandler.SetResult();
        await pendingRequest.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        await committed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await StopConsumerAsync(consuming, cancellation);
        consumer.Verify(candidate => candidate.CommitAsync(), Times.Once);
        consumer.VerifyAll();
    }

    [Fact]
    public async Task RunAsync_HandlerFails_CompletesRequestThenCommitsBatch()
    {
        var expected = new TestHandlerException();
        var committed = NewSource();
        var handlerInvocationCount = 0;
        var pendingRequest = new PendingBatchRequest<int>(42, default, _ => { });
        var consumer = CreateConsumer([pendingRequest], committed);
        var subject = new RequestBatchConsumer<int>(
            (_, _) =>
            {
                Interlocked.Increment(ref handlerInvocationCount);
                return ValueTask.FromException(expected);
            },
            10,
            NullLogger.Instance,
            typeof(int).FullName!);
        using var cancellation = new CancellationTokenSource();

        var consuming = subject.RunAsync(consumer.Object, cancellation.Token);

        var actual = await Assert.ThrowsAsync<TestHandlerException>(
            () => pendingRequest.Completion.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Same(expected, actual);
        await committed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await StopConsumerAsync(consuming, cancellation);
        Assert.Equal(1, Volatile.Read(ref handlerInvocationCount));
        consumer.Verify(candidate => candidate.CommitAsync(), Times.Once);
        consumer.VerifyAll();
    }

    [Fact]
    public async Task RunAsync_CommitFails_PropagatesConsumerFailure()
    {
        var expected = new TestCommitException();
        var pendingRequest = new PendingBatchRequest<int>(42, default, _ => { });
        var consumer = new Mock<IBufferPullConsumer<PendingBatchRequest<int>>>(MockBehavior.Strict);
        consumer
            .Setup(candidate => candidate.ConsumeAsync(It.IsAny<CancellationToken>()))
            .Returns((CancellationToken cancellationToken) =>
                YieldOnce([pendingRequest], cancellationToken));
        consumer
            .Setup(candidate => candidate.CommitAsync())
            .Returns(ValueTask.FromException(expected));
        var subject = new RequestBatchConsumer<int>(
            (_, _) => ValueTask.CompletedTask,
            10,
            NullLogger.Instance,
            typeof(int).FullName!);

        var actual = await Assert.ThrowsAsync<TestCommitException>(
            () => subject.RunAsync(consumer.Object, CancellationToken.None));

        Assert.Same(expected, actual);
        Assert.True(pendingRequest.Completion.IsCompletedSuccessfully);
        consumer.VerifyAll();
    }

    private static Mock<IBufferPullConsumer<PendingBatchRequest<int>>> CreateConsumer(
        PendingBatchRequest<int>[] pendingRequests,
        TaskCompletionSource committed)
    {
        var consumer = new Mock<IBufferPullConsumer<PendingBatchRequest<int>>>(MockBehavior.Strict);
        consumer
            .Setup(candidate => candidate.ConsumeAsync(It.IsAny<CancellationToken>()))
            .Returns((CancellationToken cancellationToken) =>
                YieldOnce(pendingRequests, cancellationToken));
        consumer
            .Setup(candidate => candidate.CommitAsync())
            .Callback(committed.SetResult)
            .Returns(ValueTask.CompletedTask);
        return consumer;
    }

    private static async IAsyncEnumerable<IEnumerable<PendingBatchRequest<int>>> YieldOnce(
        PendingBatchRequest<int>[] pendingRequests,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return pendingRequests;
        await Task.Delay(Timeout.Infinite, cancellationToken);
    }

    private static async Task StopConsumerAsync(
        Task consuming,
        CancellationTokenSource cancellation)
    {
        cancellation.Cancel();
        await consuming.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static TaskCompletionSource NewSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class TestHandlerException : Exception;

    private sealed class TestCommitException : Exception;
}
