using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
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
    public async Task DisposeAsync_ConsumerFailsSynchronously_IsolatesStartupContextAndReportsFailure()
    {
        using var activity = new Activity("initializing-request").Start();
        var ambient = new AsyncLocal<string?> { Value = "initializing-request" };
        var expected = new TestConsumerException();
        var observedActivity = activity;
        var observedAmbient = ambient.Value;
        var producer = new Mock<IBufferProducer<PendingBatchRequest<int>>>(MockBehavior.Strict);
        var consumer = new Mock<IBufferPullConsumer<PendingBatchRequest<int>>>(MockBehavior.Strict);
        consumer.Setup(candidate => candidate.ConsumeAsync(It.IsAny<CancellationToken>()))
            .Callback(() =>
            {
                observedActivity = Activity.Current;
                observedAmbient = ambient.Value;
            })
            .Throws(expected);
        var bufferQueue = new Mock<IBufferQueue>(MockBehavior.Strict);
        bufferQueue.Setup(candidate => candidate.GetProducer<PendingBatchRequest<int>>(
                RequestBatchCoordinator<int>.TopicName))
            .Returns(producer.Object);
        bufferQueue.Setup(candidate => candidate.CreatePullConsumers<PendingBatchRequest<int>>(
                It.IsAny<BufferPullConsumerOptions>(), 1))
            .Returns([consumer.Object]);

        var coordinator = new RequestBatchCoordinator<int>(
            bufferQueue.Object,
            (_, _) => ValueTask.CompletedTask,
            Options.Create(new RequestBatchOptions<int>()),
            NullLogger<RequestBatchCoordinator<int>>.Instance);

        Assert.Same(activity, Activity.Current);
        Assert.Equal("initializing-request", ambient.Value);
        var actual = await Assert.ThrowsAsync<TestConsumerException>(
            () => coordinator.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Same(expected, actual);
        Assert.Null(observedActivity);
        Assert.Null(observedAmbient);
        Assert.Same(activity, Activity.Current);
        Assert.Equal("initializing-request", ambient.Value);
        bufferQueue.VerifyAll();
        consumer.VerifyAll();
    }

    [Fact]
    public async Task DisposeAsync_ConsumerBatchListFails_ReportsFailureOnRepeatedCalls()
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

        var processing = coordinator.ExecuteAsync(42);

        var requestException = await Assert.ThrowsAsync<TestConsumerException>(
            () => processing.WaitAsync(TimeSpan.FromSeconds(5)));
        var firstDisposeException = await Assert.ThrowsAsync<TestConsumerException>(
            () => coordinator.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        var disposeException = await Assert.ThrowsAsync<TestConsumerException>(
            () => coordinator.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Same(expected, requestException);
        Assert.Same(expected, firstDisposeException);
        Assert.Same(expected, disposeException);
        bufferQueue.VerifyAll();
    }

    [Fact]
    public async Task ExecuteAsync_ConsumerFailsBeforeDispatch_FailsQueuedRequestsAndRejectsLaterSubmissions()
    {
        var expected = new TestConsumerException();
        var allSubmitted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var submittedRequests = new ConcurrentQueue<PendingBatchRequest<int>>();
        var submittedCount = 0;
        var handlerInvocationCount = 0;
        var producer = new Mock<IBufferProducer<PendingBatchRequest<int>>>(MockBehavior.Strict);
        producer
            .Setup(candidate => candidate.ProduceAsync(
                It.IsAny<PendingBatchRequest<int>>(),
                It.IsAny<CancellationToken>()))
            .Returns((PendingBatchRequest<int> request, CancellationToken _) =>
            {
                submittedRequests.Enqueue(request);
                if (Interlocked.Increment(ref submittedCount) == 2)
                {
                    allSubmitted.SetResult();
                }

                return ValueTask.CompletedTask;
            });
        var consumer = new Mock<IBufferPullConsumer<PendingBatchRequest<int>>>(MockBehavior.Strict);
        consumer
            .Setup(candidate => candidate.ConsumeAsync(It.IsAny<CancellationToken>()))
            .Returns((CancellationToken cancellationToken) =>
                ConsumeAndThrowAsync<PendingBatchRequest<int>>(allSubmitted.Task, expected, cancellationToken));

        var bufferQueue = new Mock<IBufferQueue>(MockBehavior.Strict);
        bufferQueue
            .Setup(candidate => candidate.GetProducer<PendingBatchRequest<int>>(
                RequestBatchCoordinator<int>.TopicName))
            .Returns(producer.Object);
        bufferQueue
            .Setup(candidate => candidate.CreatePullConsumers<PendingBatchRequest<int>>(
                It.Is<BufferPullConsumerOptions>(options =>
                    options.TopicName == RequestBatchCoordinator<int>.TopicName &&
                    options.GroupName == RequestBatchCoordinator<int>.ConsumerGroupName &&
                    options.BatchSize == 128 &&
                    options.AutoCommit),
                1))
            .Returns([consumer.Object]);

        var coordinator = new RequestBatchCoordinator<int>(
            bufferQueue.Object,
            (_, _) =>
            {
                Interlocked.Increment(ref handlerInvocationCount);
                return ValueTask.CompletedTask;
            },
            Options.Create(new RequestBatchOptions<int>()),
            NullLogger<RequestBatchCoordinator<int>>.Instance);

        var first = coordinator.ExecuteAsync(1);
        var second = coordinator.ExecuteAsync(2);
        await allSubmitted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var firstException = await Assert.ThrowsAsync<TestConsumerException>(
            () => first.WaitAsync(TimeSpan.FromSeconds(5)));
        var secondException = await Assert.ThrowsAsync<TestConsumerException>(
            () => second.WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Same(expected, firstException);
        Assert.Same(expected, secondException);
        Assert.Equal(0, Volatile.Read(ref handlerInvocationCount));
        Assert.Equal(2, submittedRequests.Count);
        foreach (var request in submittedRequests)
        {
            var requestException = await Assert.ThrowsAsync<TestConsumerException>(
                () => request.Completion.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Same(expected, requestException);
        }

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => coordinator.ExecuteAsync(3).WaitAsync(TimeSpan.FromSeconds(5)));

        var firstDisposeException = await Assert.ThrowsAsync<TestConsumerException>(
            () => coordinator.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        var secondDisposeException = await Assert.ThrowsAsync<TestConsumerException>(
            () => coordinator.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Same(expected, firstDisposeException);
        Assert.Same(expected, secondDisposeException);
        producer.VerifyAll();
        consumer.VerifyAll();
        bufferQueue.VerifyAll();
    }

    private static async IAsyncEnumerable<IReadOnlyList<T>> ConsumeAndFailAsync<T>(
        Task<T> submitted,
        Exception exception,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var request = await submitted.WaitAsync(cancellationToken);
        yield return new FailingReadOnlyList<T>(request, exception);
    }

    private static async IAsyncEnumerable<IReadOnlyList<T>> ConsumeAndThrowAsync<T>(
        Task submitted,
        Exception exception,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await submitted.WaitAsync(cancellationToken);
        if (exception is not null)
        {
            throw exception;
        }

        yield break;
    }

    private sealed class FailingReadOnlyList<T>(T request, Exception exception) : IReadOnlyList<T>
    {
        public int Count => 2;

        public T this[int index] => index switch
        {
            0 => request,
            1 => throw exception,
            _ => throw new ArgumentOutOfRangeException(nameof(index))
        };

        public IEnumerator<T> GetEnumerator()
        {
            for (var index = 0; index < Count; index++)
            {
                yield return this[index];
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
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

        public IAsyncEnumerable<IReadOnlyList<PendingBatchRequest<int>>> ConsumeAsync(
            CancellationToken cancellationToken = default) =>
            ConsumeAndFailAsync(submitted, exception, cancellationToken);

        public ValueTask CommitAsync() => ValueTask.CompletedTask;
    }

    private sealed class TestConsumerException : Exception;
}
