using BufferQueue;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RequestBatcher.PendingRequests;

namespace RequestBatcher.Tests.PendingRequests;

public sealed class PendingRequestProducerTests
{
    [Fact]
    public async Task ProduceAsync_CallerCanceledWhileWaiting_ForwardsCancellation()
    {
        var observedCancellation = new TaskCompletionSource<CancellationToken>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var producer = new Mock<IBufferProducer<PendingBatchRequest<int>>>(MockBehavior.Strict);
        producer
            .Setup(candidate => candidate.ProduceAsync(
                It.IsAny<PendingBatchRequest<int>>(),
                It.IsAny<CancellationToken>()))
            .Returns((PendingBatchRequest<int> _, CancellationToken cancellationToken) =>
            {
                observedCancellation.SetResult(cancellationToken);
                return new ValueTask(Task.Delay(Timeout.Infinite, cancellationToken));
            });

        using var stopping = new CancellationTokenSource();
        using var callerCancellation = new CancellationTokenSource();
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pendingRequest = new PendingBatchRequest<int>(
            42,
            callerCancellation.Token,
            _ => finished.SetResult());
        var subject = CreateSubject(producer.Object, stopping.Token);

        var processing = subject.ProduceAsync(pendingRequest, callerCancellation.Token);
        var forwardedCancellation = await observedCancellation.Task.WaitAsync(TimeSpan.FromSeconds(5));
        callerCancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => processing);
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(processing.IsCanceled);
        Assert.True(forwardedCancellation.IsCancellationRequested);
        producer.VerifyAll();
    }

    [Fact]
    public async Task ProduceAsync_CoordinatorStopsWhileWaiting_ReturnsObjectDisposedException()
    {
        var observedCancellation = new TaskCompletionSource<CancellationToken>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var producer = new Mock<IBufferProducer<PendingBatchRequest<int>>>(MockBehavior.Strict);
        producer
            .Setup(candidate => candidate.ProduceAsync(
                It.IsAny<PendingBatchRequest<int>>(),
                It.IsAny<CancellationToken>()))
            .Returns((PendingBatchRequest<int> _, CancellationToken cancellationToken) =>
            {
                observedCancellation.SetResult(cancellationToken);
                return new ValueTask(Task.Delay(Timeout.Infinite, cancellationToken));
            });

        using var stopping = new CancellationTokenSource();
        var pendingRequest = new PendingBatchRequest<int>(42, default, _ => { });
        var subject = CreateSubject(producer.Object, stopping.Token);

        var processing = subject.ProduceAsync(pendingRequest, default);
        var forwardedCancellation = await observedCancellation.Task.WaitAsync(TimeSpan.FromSeconds(5));
        stopping.Cancel();

        var exception = await Assert.ThrowsAsync<ObjectDisposedException>(() => processing);
        Assert.Equal(typeof(RequestBatchCoordinator<int>).FullName, exception.ObjectName);
        Assert.True(forwardedCancellation.IsCancellationRequested);
        producer.VerifyAll();
    }

    [Fact]
    public async Task ProduceAsync_BufferQueueRejectsBatch_MapsPublicQueueFullException()
    {
        var producer = new Mock<IBufferProducer<PendingBatchRequest<int>>>(MockBehavior.Strict);
        producer
            .Setup(candidate => candidate.ProduceAsync(
                It.IsAny<ReadOnlyMemory<PendingBatchRequest<int>>>(),
                It.IsAny<CancellationToken>()))
            .Throws(new BufferQueueFullException("full"));

        using var stopping = new CancellationTokenSource();
        var finishedCount = 0;
        var completion = new BatchSubmissionCompletion(3, default);
        var pendingRequests = Enumerable.Range(1, 3)
            .Select(request => new PendingBatchRequest<int>(
                request,
                default,
                _ => Interlocked.Increment(ref finishedCount),
                completion))
            .ToArray();
        var subject = CreateSubject(producer.Object, stopping.Token, capacity: 8);

        var exception = await Assert.ThrowsAsync<RequestBatchQueueFullException>(
            () => subject.ProduceAsync(pendingRequests, default));

        Assert.Equal(8, exception.Capacity);
        Assert.Equal(3, exception.RequestedCount);
        Assert.Equal(3, Volatile.Read(ref finishedCount));
        producer.VerifyAll();
    }

    [Fact]
    public async Task ProduceAsync_BatchProducerFailsAsynchronously_PropagatesOriginalException()
    {
        var expected = new TestProducerException();
        var producer = new Mock<IBufferProducer<PendingBatchRequest<int>>>(MockBehavior.Strict);
        producer
            .Setup(candidate => candidate.ProduceAsync(
                It.IsAny<ReadOnlyMemory<PendingBatchRequest<int>>>(),
                It.IsAny<CancellationToken>()))
            .Returns(ValueTask.FromException(expected));

        using var stopping = new CancellationTokenSource();
        var completion = new BatchSubmissionCompletion(3, default);
        var pendingRequests = Enumerable.Range(1, 3)
            .Select(request => new PendingBatchRequest<int>(
                request,
                default,
                _ => { },
                completion))
            .ToArray();
        var subject = CreateSubject(producer.Object, stopping.Token);

        var actual = await Assert.ThrowsAsync<TestProducerException>(
            () => subject.ProduceAsync(pendingRequests, default));

        Assert.Same(expected, actual);
        Assert.Equal(expected, Assert.Single(completion.Task.Exception!.InnerExceptions));
        producer.VerifyAll();
    }

    [Fact]
    public async Task ProduceAsync_BatchProducerWaits_PreservesAllCompletionExceptions()
    {
        var produceCompletion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var producer = new Mock<IBufferProducer<PendingBatchRequest<int>>>(MockBehavior.Strict);
        producer
            .Setup(candidate => candidate.ProduceAsync(
                It.IsAny<ReadOnlyMemory<PendingBatchRequest<int>>>(),
                It.IsAny<CancellationToken>()))
            .Returns(new ValueTask(produceCompletion.Task));

        using var stopping = new CancellationTokenSource();
        var first = new TestProducerException();
        var second = new TestProducerException();
        var completion = new BatchSubmissionCompletion(2, default);
        var pendingRequests = Enumerable.Range(1, 2)
            .Select(request => new PendingBatchRequest<int>(
                request,
                default,
                _ => { },
                completion))
            .ToArray();
        var subject = CreateSubject(producer.Object, stopping.Token);

        var processing = subject.ProduceAsync(pendingRequests, default);
        produceCompletion.SetResult();
        Assert.True(pendingRequests[0].TryStartProcessing());
        Assert.True(pendingRequests[1].TryStartProcessing());
        pendingRequests[0].CompleteWithError(first);
        pendingRequests[1].CompleteWithError(second);

        await Assert.ThrowsAsync<TestProducerException>(() => processing);
        Assert.Equal(2, processing.Exception!.InnerExceptions.Count);
        Assert.Contains(first, processing.Exception.InnerExceptions);
        Assert.Contains(second, processing.Exception.InnerExceptions);
        producer.VerifyAll();
    }

    [Fact]
    public async Task ProduceAsync_ProducerFailsAsynchronously_PropagatesOriginalException()
    {
        var expected = new TestProducerException();
        var producer = new Mock<IBufferProducer<PendingBatchRequest<int>>>(MockBehavior.Strict);
        producer
            .Setup(candidate => candidate.ProduceAsync(
                It.IsAny<PendingBatchRequest<int>>(),
                It.IsAny<CancellationToken>()))
            .Returns(ValueTask.FromException(expected));

        using var stopping = new CancellationTokenSource();
        var pendingRequest = new PendingBatchRequest<int>(42, default, _ => { });
        var subject = CreateSubject(producer.Object, stopping.Token);

        var actual = await Assert.ThrowsAsync<TestProducerException>(
            () => subject.ProduceAsync(pendingRequest, default));

        Assert.Same(expected, actual);
        producer.VerifyAll();
    }

    private static PendingRequestProducer<int> CreateSubject(
        IBufferProducer<PendingBatchRequest<int>> producer,
        CancellationToken stoppingToken,
        int capacity = 16) =>
        new(
            producer,
            NullLogger.Instance,
            typeof(int).FullName!,
            capacity,
            stoppingToken);

    private sealed class TestProducerException : Exception;
}
