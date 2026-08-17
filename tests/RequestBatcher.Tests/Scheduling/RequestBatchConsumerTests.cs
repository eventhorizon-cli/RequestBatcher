using Microsoft.Extensions.Logging.Abstractions;
using RequestBatcher.PendingRequests;
using RequestBatcher.Scheduling;

namespace RequestBatcher.Tests.Scheduling;

public sealed class RequestBatchConsumerTests
{
    [Fact]
    public async Task ProcessAsync_HandlerIsRunning_CompletesRequestsAfterHandler()
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

        var processing = subject.ProcessAsync([pendingRequest], CancellationToken.None).AsTask();
        await handlerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(pendingRequest.Completion.IsCompleted);
        releaseHandler.SetResult();

        await processing.WaitAsync(TimeSpan.FromSeconds(5));
        await pendingRequest.Completion.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ProcessAsync_HandlerFails_CompletesRequestWithOriginalException()
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

        await subject.ProcessAsync([pendingRequest], CancellationToken.None).AsTask();

        var actual = await Assert.ThrowsAsync<TestHandlerException>(
            () => pendingRequest.Completion.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Same(expected, actual);
        Assert.Equal(1, Volatile.Read(ref handlerInvocationCount));
    }

    [Fact]
    public async Task ProcessAsync_RequestCanceledBeforeDispatch_SkipsHandler()
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

        await subject.ProcessAsync([pendingRequest], CancellationToken.None).AsTask();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => pendingRequest.Completion.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, Volatile.Read(ref handlerInvocationCount));
    }

    private static TaskCompletionSource NewSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class TestHandlerException : Exception;
}
