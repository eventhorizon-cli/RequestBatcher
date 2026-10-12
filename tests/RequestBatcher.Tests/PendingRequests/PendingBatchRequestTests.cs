using RequestBatcher.PendingRequests;

namespace RequestBatcher.Tests.PendingRequests;

public sealed class PendingBatchRequestTests
{
    [Fact]
    public async Task Constructor_AlreadyCanceledToken_CompletesCanceledWithOriginalToken()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var finishedCount = 0;

        var pendingRequest = new PendingBatchRequest<int>(
            42,
            cancellation.Token,
            _ => Interlocked.Increment(ref finishedCount));

        Assert.True(pendingRequest.Completion.IsCanceled);
        var actual = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => pendingRequest.Completion);

        Assert.Equal(cancellation.Token, actual.CancellationToken);
        Assert.Equal(0, Volatile.Read(ref finishedCount));
        Assert.False(pendingRequest.TryStartProcessing());

        pendingRequest.FinishCanceledRequest();
        pendingRequest.FinishCanceledRequest();

        Assert.Equal(1, Volatile.Read(ref finishedCount));
    }

    [Fact]
    public async Task CancelWhileQueued_RequestCanceled_CompletesWithOriginalTokenAndFinishesOnce()
    {
        using var cancellation = new CancellationTokenSource();
        var finishedCount = 0;
        var pendingRequest = new PendingBatchRequest<int>(
            42,
            cancellation.Token,
            _ => Interlocked.Increment(ref finishedCount));

        cancellation.Cancel();

        var actual = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => pendingRequest.Completion);
        Assert.Equal(cancellation.Token, actual.CancellationToken);
        Assert.Equal(0, Volatile.Read(ref finishedCount));
        Assert.False(pendingRequest.TryStartProcessing());

        pendingRequest.FinishCanceledRequest();
        pendingRequest.FinishCanceledRequest();

        Assert.Equal(1, Volatile.Read(ref finishedCount));
    }

    [Fact]
    public async Task CancelAfterProcessingStarts_HandlerSucceeds_PreservesSuccess()
    {
        using var cancellation = new CancellationTokenSource();
        var finishedCount = 0;
        var pendingRequest = new PendingBatchRequest<int>(
            42,
            cancellation.Token,
            _ => Interlocked.Increment(ref finishedCount));

        Assert.True(pendingRequest.TryStartProcessing());
        cancellation.Cancel();
        pendingRequest.CompleteSuccessfully();

        await pendingRequest.Completion;

        Assert.Equal(1, Volatile.Read(ref finishedCount));
    }

    [Fact]
    public async Task CancelAfterProcessingStarts_HandlerFails_PreservesFailure()
    {
        using var cancellation = new CancellationTokenSource();
        var expected = new TestHandlerException();
        var finishedCount = 0;
        var pendingRequest = new PendingBatchRequest<int>(
            42,
            cancellation.Token,
            _ => Interlocked.Increment(ref finishedCount));

        Assert.True(pendingRequest.TryStartProcessing());
        cancellation.Cancel();
        pendingRequest.CompleteWithError(expected);

        var actual = await Assert.ThrowsAsync<TestHandlerException>(
            () => pendingRequest.Completion);

        Assert.Same(expected, actual);
        Assert.Equal(1, Volatile.Read(ref finishedCount));
    }

    private sealed class TestHandlerException : Exception;
}
