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
    public async Task CompleteCanceled_InterfaceCall_WaitsForAllItemsAndUsesSubmissionToken()
    {
        using var submissionCancellation = new CancellationTokenSource();
        using var itemCancellation = new CancellationTokenSource();
        submissionCancellation.Cancel();
        itemCancellation.Cancel();
        var subject = new BatchSubmissionCompletion(2, submissionCancellation.Token);
        IRequestCompletion completion = subject;

        completion.CompleteCanceled(itemCancellation.Token);

        Assert.False(subject.Task.IsCompleted);
        completion.CompleteSuccessfully();

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => subject.Task);
        Assert.True(subject.Task.IsCanceled);
        Assert.Equal(submissionCancellation.Token, exception.CancellationToken);
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
    [InlineData(1)]
    [InlineData(8)]
    public async Task CompleteWithError_ConcurrentRepeatedFailures_WaitsForFinalItem(int distinctFailureCount)
    {
        const int workerCount = 3;
        const int reportsPerWorker = 64;
        var failures = Enumerable.Range(0, distinctFailureCount)
            .Select(_ => new FirstTestException())
            .ToArray();
        var subject = new BatchSubmissionCompletion(workerCount * reportsPerWorker + 1, default);
        using var start = new Barrier(workerCount + 1);

        var workers = Enumerable.Range(0, workerCount).Select(worker => Task.Run(() =>
        {
            Assert.True(start.SignalAndWait(TimeSpan.FromSeconds(5)));
            for (var report = 0; report < reportsPerWorker; report++)
            {
                subject.CompleteWithError(failures[(worker + report) % failures.Length]);
            }
        })).ToArray();

        Assert.True(start.SignalAndWait(TimeSpan.FromSeconds(5)));
        await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(subject.Task.IsCompleted);

        subject.CompleteSuccessfully();

        await Assert.ThrowsAsync<FirstTestException>(() => subject.Task);
        var actual = subject.Task.Exception!.InnerExceptions;
        Assert.Equal(distinctFailureCount, actual.Count);
        foreach (var failure in failures)
        {
            Assert.Contains(actual, candidate => ReferenceEquals(candidate, failure));
        }
    }

    [Fact]
    public async Task CompleteWithError_ConcurrentCancellationAndEqualValuedFailures_PreservesAllFailures()
    {
        const int workerCount = 4;
        const int reportsPerWorker = 64;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var failures = Enumerable.Range(0, 4).Select(_ => new EqualValueTestException()).ToArray();
        var subject = new BatchSubmissionCompletion(workerCount * reportsPerWorker, cancellation.Token);
        using var start = new Barrier(workerCount + 1);

        var workers = Enumerable.Range(0, workerCount).Select(worker => Task.Run(() =>
        {
            Assert.True(start.SignalAndWait(TimeSpan.FromSeconds(5)));
            for (var report = 0; report < reportsPerWorker; report++)
            {
                switch (report % 3)
                {
                    case 0:
                        subject.CompleteWithError(failures[(worker + report) % failures.Length]);
                        break;
                    case 1:
                        subject.CompleteCanceled();
                        break;
                    default:
                        subject.CompleteSuccessfully();
                        break;
                }
            }
        })).ToArray();

        Assert.True(start.SignalAndWait(TimeSpan.FromSeconds(5)));
        await Assert.ThrowsAsync<EqualValueTestException>(
            () => subject.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        var actual = subject.Task.Exception!.InnerExceptions;
        await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(failures.Length, actual.Count);
        foreach (var failure in failures)
        {
            Assert.Contains(actual, candidate => ReferenceEquals(candidate, failure));
        }
    }

    [Fact]
    public async Task CompleteWithError_EqualValuedDistinctExceptions_PreservesReferenceIdentity()
    {
        var first = new EqualValueTestException();
        var second = new EqualValueTestException();
        var subject = new BatchSubmissionCompletion(4, default);

        subject.CompleteWithError(first);
        subject.CompleteWithError(second);
        subject.CompleteWithError(first);
        subject.CompleteWithError(second);

        var actual = await Assert.ThrowsAsync<EqualValueTestException>(() => subject.Task);
        Assert.True(ReferenceEquals(actual, first) || ReferenceEquals(actual, second));
        var failures = subject.Task.Exception!.InnerExceptions;
        Assert.Equal(2, failures.Count);
        Assert.Contains(failures, failure => ReferenceEquals(failure, first));
        Assert.Contains(failures, failure => ReferenceEquals(failure, second));
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

    private sealed class EqualValueTestException : Exception
    {
        public override bool Equals(object? obj) => obj is EqualValueTestException;

        public override int GetHashCode() => 0;
    }
}
