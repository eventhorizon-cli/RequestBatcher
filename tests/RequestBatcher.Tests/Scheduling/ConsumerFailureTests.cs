using System.Runtime.CompilerServices;
using BufferQueue;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using RequestBatcher.PendingRequests;

namespace RequestBatcher.Tests.Scheduling;

public sealed class ConsumerFailureTests
{
    [Fact]
    public async Task ConsumerEnumerationFails_CompletesAcceptedRequestAndStop()
    {
        var expected = new TestConsumerException();
        var submitted = new TaskCompletionSource<PendingBatchRequest<int>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var producer = new TestProducer(submitted);
        var consumer = new TestConsumer(submitted.Task, expected);

        var bufferQueue = new Mock<IBufferQueue>(MockBehavior.Strict);
        bufferQueue
            .Setup(candidate => candidate.GetProducer<PendingBatchRequest<int>>(
                RequestBatchCoordinator<int>.TopicName))
            .Returns(producer);
        bufferQueue
            .Setup(candidate => candidate.CreatePullConsumers<PendingBatchRequest<int>>(
                It.Is<BufferPullConsumerOptions>(options =>
                    options.TopicName == RequestBatchCoordinator<int>.TopicName &&
                    options.GroupName == RequestBatchCoordinator<int>.ConsumerGroupName &&
                    options.BatchSize == 128 &&
                    options.AutoCommit),
                1))
            .Returns([consumer]);

        var coordinator = new RequestBatchCoordinator<int>(
            bufferQueue.Object,
            (_, _) => ValueTask.CompletedTask,
            Options.Create(new RequestBatchOptions<int> { MaxConcurrency = 64 }),
            NullLogger<RequestBatchCoordinator<int>>.Instance);

        var processing = coordinator.ProcessAsync(42);

        var requestException = await Assert.ThrowsAsync<TestConsumerException>(
            () => processing.WaitAsync(TimeSpan.FromSeconds(5)));
        var stopException = await Assert.ThrowsAsync<TestConsumerException>(
            () => coordinator.StopAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        var disposeException = await Assert.ThrowsAsync<TestConsumerException>(
            () => coordinator.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Same(expected, requestException);
        Assert.Same(expected, stopException);
        Assert.Same(expected, disposeException);
        bufferQueue.VerifyAll();
    }

    private static async IAsyncEnumerable<IEnumerable<T>> ConsumeAndFailAsync<T>(
        Task<T> submitted,
        Exception exception,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var request = await submitted.WaitAsync(cancellationToken);
        yield return EnumerateAndFail(request, exception);
    }

    private static IEnumerable<T> EnumerateAndFail<T>(T request, Exception exception)
    {
        yield return request;
        throw exception;
    }

    private sealed class TestProducer(
        TaskCompletionSource<PendingBatchRequest<int>> submitted)
        : IBufferProducer<PendingBatchRequest<int>>
    {
        public string TopicName => RequestBatchCoordinator<int>.TopicName;

        public ValueTask<bool> TryProduceAsync(
            PendingBatchRequest<int> item,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            submitted.TrySetResult(item);
            return ValueTask.FromResult(true);
        }

        public ValueTask<bool> TryProduceAsync(
            ReadOnlyMemory<PendingBatchRequest<int>> items,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask ProduceAsync(
            PendingBatchRequest<int> item,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            submitted.TrySetResult(item);
            return ValueTask.CompletedTask;
        }

        public ValueTask ProduceAsync(
            ReadOnlyMemory<PendingBatchRequest<int>> items,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class TestConsumer(
        Task<PendingBatchRequest<int>> submitted,
        Exception exception)
        : IBufferPullConsumer<PendingBatchRequest<int>>
    {
        public string TopicName => RequestBatchCoordinator<int>.TopicName;

        public string GroupName => RequestBatchCoordinator<int>.ConsumerGroupName;

        public IAsyncEnumerable<IEnumerable<PendingBatchRequest<int>>> ConsumeAsync(
            CancellationToken cancellationToken = default) =>
            ConsumeAndFailAsync(submitted, exception, cancellationToken);

        public ValueTask CommitAsync() => ValueTask.CompletedTask;
    }

    private sealed class TestConsumerException : Exception;
}
