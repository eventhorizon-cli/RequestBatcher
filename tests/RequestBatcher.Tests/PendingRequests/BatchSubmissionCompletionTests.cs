using RequestBatcher.PendingRequests;

namespace RequestBatcher.Tests.PendingRequests;

public sealed class BatchSubmissionCompletionTests
{
    [Fact]
    public async Task CompleteSuccessfully_AllRequestsFinish_CompletesAfterLastRequest()
    {
        var subject = new BatchSubmissionCompletion(3, default);

        subject.CompleteSuccessfully();
        subject.CompleteSuccessfully();

        Assert.False(subject.Task.IsCompleted);

        subject.CompleteSuccessfully();

        await subject.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(subject.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task CompleteWithError_DistinctFailures_AggregatesOriginalExceptions()
    {
        var first = new FirstTestException();
        var second = new SecondTestException();
        var subject = new BatchSubmissionCompletion(3, default);

        subject.CompleteWithError(first);
        subject.CompleteSuccessfully();

        Assert.False(subject.Task.IsCompleted);

        subject.CompleteWithError(second);

        var actual = await Assert.ThrowsAnyAsync<Exception>(() => subject.Task);
        Assert.True(ReferenceEquals(actual, first) || ReferenceEquals(actual, second));
        Assert.Equal(2, subject.Task.Exception!.InnerExceptions.Count);
        Assert.Contains(first, subject.Task.Exception.InnerExceptions);
        Assert.Contains(second, subject.Task.Exception.InnerExceptions);
    }

    [Fact]
    public async Task CompleteWithError_RepeatedExceptionReference_RecordsFailureOnce()
    {
        var expected = new FirstTestException();
        var subject = new BatchSubmissionCompletion(2, default);

        subject.CompleteWithError(expected);
        subject.CompleteWithError(expected);

        var actual = await Assert.ThrowsAsync<FirstTestException>(() => subject.Task);
        Assert.Same(expected, actual);
        Assert.Equal(expected, Assert.Single(subject.Task.Exception!.InnerExceptions));
    }

    [Fact]
    public async Task CompleteCanceled_RemainingRequestFails_FailureTakesPrecedence()
    {
        var expected = new FirstTestException();
        using var cancellation = new CancellationTokenSource();
        var subject = new BatchSubmissionCompletion(2, cancellation.Token);

        cancellation.Cancel();
        subject.CompleteCanceled();

        Assert.False(subject.Task.IsCompleted);

        subject.CompleteWithError(expected);

        var actual = await Assert.ThrowsAsync<FirstTestException>(() => subject.Task);
        Assert.Same(expected, actual);
        Assert.True(subject.Task.IsFaulted);
    }

    [Fact]
    public async Task CompleteCanceled_RemainingRequestSucceeds_CancelsWithCallerToken()
    {
        using var cancellation = new CancellationTokenSource();
        var subject = new BatchSubmissionCompletion(2, cancellation.Token);

        cancellation.Cancel();
        subject.CompleteCanceled();

        Assert.False(subject.Task.IsCompleted);

        subject.CompleteSuccessfully();

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => subject.Task);
        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.True(subject.Task.IsCanceled);
    }

    [Fact]
    public async Task CompleteRequest_MixedOutcomes_WaitsForAllAndPreservesEveryFailure()
    {
        var first = new FirstTestException();
        var second = new SecondTestException();
        var subject = new BatchSubmissionCompletion(4, default);

        subject.CompleteCanceled();
        subject.CompleteWithError(first);
        subject.CompleteSuccessfully();

        Assert.False(subject.Task.IsCompleted);

        subject.CompleteWithError(second);

        var actual = await Assert.ThrowsAnyAsync<Exception>(() => subject.Task);
        Assert.True(ReferenceEquals(actual, first) || ReferenceEquals(actual, second));
        Assert.Equal(2, subject.Task.Exception!.InnerExceptions.Count);
        Assert.Contains(first, subject.Task.Exception.InnerExceptions);
        Assert.Contains(second, subject.Task.Exception.InnerExceptions);
        Assert.True(subject.Task.IsFaulted);
    }

    [Fact]
    public async Task CompleteWithError_ConcurrentCompletions_RetainsEveryDistinctFailure()
    {
        const int requestCount = 64;
        var exceptions = Enumerable.Range(0, requestCount / 2)
            .Select(_ => new FirstTestException())
            .ToArray();
        var subject = new BatchSubmissionCompletion(requestCount, default);

        Parallel.For(0, requestCount, index =>
        {
            if (index % 2 == 0)
            {
                subject.CompleteWithError(exceptions[index / 2]);
            }
            else
            {
                subject.CompleteSuccessfully();
            }
        });

        await Assert.ThrowsAsync<FirstTestException>(() => subject.Task);
        var actualExceptions = subject.Task.Exception!.InnerExceptions;
        Assert.Equal(exceptions.Length, actualExceptions.Count);
        foreach (var exception in exceptions)
        {
            Assert.Contains(exception, actualExceptions);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_NonPositiveRequestCount_Throws(int requestCount)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new BatchSubmissionCompletion(requestCount, default));
    }

    private sealed class FirstTestException : Exception;

    private sealed class SecondTestException : Exception;
}
