using System.Collections.Concurrent;
using System.Diagnostics;
using RequestBatcher.Diagnostics;
using RequestBatcher.Diagnostics.Tracing;
using RequestBatcher.PendingRequests;

namespace RequestBatcher.Tests.Diagnostics;

[Collection("Tracing")]
public sealed class SharedCompletionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TraceCall_CompletionBeforeOrAfterSubmission_ReturnsOriginalTask(bool completeBeforeAttach)
    {
        using var capture = new ActivityCapture();
        TracedRequestCompletion? completion = null;
        var task = RequestBatcherTracing.TraceCall(
            new RequestBatcherMetadata("test"),
            completeBeforeAttach,
            (completeBeforeReturn, _, observer) =>
            {
                completion = new TracedRequestCompletion(observer!);
                capture.TaskCompletionState = () => completion.Task.IsCompleted;
                if (completeBeforeReturn)
                {
                    completion.CompleteSuccessfully();
                    Assert.False(completion.Task.IsCompleted);
                }

                return completion.Task;
            });

        Assert.NotNull(completion);
        Assert.Same(completion.Task, task);

        if (!completeBeforeAttach)
        {
            completion.CompleteSuccessfully();
        }

        await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(capture.Stopped);
        Assert.False(Assert.Single(capture.TaskWasCompletedAtStop));
    }

    [Fact]
    public async Task TracedRequestCompletion_CompletionBeforeAttach_StopsOnlyOnce()
    {
        using var capture = new ActivityCapture();
        var activity = StartActivity();
        var observer = new CallCompletionObserver(activity, null);
        var completion = new TracedRequestCompletion(observer);

        completion.CompleteSuccessfully();
        completion.CompleteSuccessfully();
        Assert.False(completion.Task.IsCompleted);
        Assert.Empty(capture.Stopped);

        Assert.True(observer.TryAttach(completion.Task));
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(capture.Stopped);

        observer.OnCompleted((IRequestCompletionNotification)completion);
        Assert.Single(capture.Stopped);
    }

    [Fact]
    public async Task TracedRequestCompletion_CompletionOnWorker_RestoresWorkerContextAndUsesCapturedContext()
    {
        var marker = new AsyncLocal<string?>();
        using var capture = new ActivityCapture(() => marker.Value);
        marker.Value = "submitter";
        using var submitterParent = new Activity("submitter-parent").Start();
        var activity = StartActivity();
        var observer = new CallCompletionObserver(activity, null);
        var completion = new TracedRequestCompletion(observer);
        capture.TaskCompletionState = () => completion.Task.IsCompleted;
        // Completion retains its captured context independently of the caller's ambient Activity.
        Activity.Current = submitterParent;
        Assert.True(observer.TryAttach(completion.Task));

        var workerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workerContext = new TaskCompletionSource<(string? Marker, string? Activity)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = Task.Run(() =>
        {
            marker.Value = "worker";
            using var workerParent = new Activity("worker-parent").Start();
            workerStarted.SetResult();
            completion.CompleteSuccessfully();
            workerContext.SetResult((marker.Value, Activity.Current?.OperationName));
        });

        await workerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await worker.WaitAsync(TimeSpan.FromSeconds(5));
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stopped = Assert.Single(capture.Stopped);
        Assert.Equal("submitter", stopped.GetTagItem("test.marker"));
        Assert.Equal("completion-test", stopped.GetTagItem("test.current"));
        Assert.Equal(activity.OperationName, stopped.OperationName);
        Assert.False(capture.TaskWasCompletedAtStop!.Single());

        var observedWorkerContext = await workerContext.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("worker", observedWorkerContext.Marker);
        Assert.Equal("worker-parent", observedWorkerContext.Activity);
        Assert.Same(submitterParent, Activity.Current);
        Assert.Equal("submitter", marker.Value);
    }

    [Fact]
    public async Task TracedRequestCompletion_NonDirectTask_PublishesWithoutStoppingSpan()
    {
        using var capture = new ActivityCapture();
        var activity = StartActivity();
        var observer = new CallCompletionObserver(activity, null);
        var completion = new TracedRequestCompletion(observer);
        var returnedTask = Task.CompletedTask;

        Assert.False(observer.TryAttach(returnedTask));
        completion.CompleteSuccessfully();

        await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(capture.Stopped);
        activity.Dispose();
    }

    [Fact]
    public async Task CallCompletionObserver_RepeatedAttach_ReturnsOriginalDecision()
    {
        using var capture = new ActivityCapture();
        var activity = StartActivity();
        var observer = new CallCompletionObserver(activity, null);
        var completion = new TracedRequestCompletion(observer);

        Assert.False(observer.TryAttach(Task.CompletedTask));
        Assert.False(observer.TryAttach(completion.Task));

        completion.CompleteSuccessfully();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Empty(capture.Stopped);
        activity.Dispose();
    }

    [Fact]
    public async Task CallCompletionObserver_SuppressedExecutionContext_RestoresAmbientActivity()
    {
        using var capture = new ActivityCapture();
        var previous = Activity.Current;
        var activity = StartActivity();
        CallCompletionObserver observer;
        try
        {
            using (ExecutionContext.SuppressFlow())
            {
                observer = new CallCompletionObserver(activity, null);
            }

            var completion = new TracedRequestCompletion(observer);
            Assert.True(observer.TryAttach(completion.Task));

            using var workerParent = new Activity("worker-parent").Start();
            completion.CompleteSuccessfully();
            await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Same(workerParent, Activity.Current);
            Assert.Single(capture.Stopped);
        }
        finally
        {
            Activity.Current = previous;
        }
    }

    [Fact]
    public async Task TracedResponseCompletion_AttachAndPublish_ReturnsTypedResultAndStopsSpan()
    {
        using var capture = new ActivityCapture();
        var activity = StartActivity();
        var observer = new CallCompletionObserver(activity, null);
        var entry = new RequestBatchEntry<int, string>(1);
        entry.SetResponse("answer");
        var completion = new TracedResponseCompletion<int, string>(entry, observer);

        Assert.True(observer.TryAttach(completion.Task));
        completion.CompleteSuccessfully();

        Assert.Equal("answer", await completion.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Single(capture.Stopped);
    }

    [Fact]
    public async Task TracedResponseCompletion_Error_PublishesOriginalFailureAndStopsSpan()
    {
        using var capture = new ActivityCapture();
        var expected = new InvalidOperationException("response failed");
        var activity = StartActivity();
        var observer = new CallCompletionObserver(activity, null);
        var completion = new TracedResponseCompletion<int, string>(new RequestBatchEntry<int, string>(1), observer);

        Assert.True(observer.TryAttach(completion.Task));
        completion.CompleteWithError(expected);

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() => completion.Task);
        Assert.Same(expected, actual);
        Assert.True(completion.Task.IsFaulted);
        var span = Assert.Single(capture.Stopped);
        Assert.Equal(ActivityStatusCode.Error, span.Status);
        Assert.Equal(nameof(InvalidOperationException), span.GetTagItem("error.type"));
    }

    [Fact]
    public async Task TracedResponseCompletion_Canceled_PreservesTokenAndStopsSpan()
    {
        using var capture = new ActivityCapture();
        using var cancellation = new CancellationTokenSource();
        var activity = StartActivity();
        var observer = new CallCompletionObserver(activity, null);
        var completion = new TracedResponseCompletion<int, string>(new RequestBatchEntry<int, string>(1), observer);
        Assert.True(observer.TryAttach(completion.Task));

        completion.CompleteCanceled(cancellation.Token);

        var actual = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => completion.Task);
        Assert.Equal(cancellation.Token, actual.CancellationToken);
        Assert.True(completion.Task.IsCanceled);
        var span = Assert.Single(capture.Stopped);
        Assert.Equal(true, span.GetTagItem("requestbatcher.canceled"));
    }

    [Fact]
    public async Task RequestBatcherTracing_GenericObservedCompletion_ReturnsOriginalTypedTask()
    {
        using var capture = new ActivityCapture();
        TracedResponseCompletion<int, string>? completion = null;
        var returned = RequestBatcherTracing.TraceCall(
            new RequestBatcherMetadata("typed-observed"),
            7,
            (request, _, observer) =>
            {
                var entry = new RequestBatchEntry<int, string>(request);
                entry.SetResponse("typed-result");
                completion = new TracedResponseCompletion<int, string>(entry, observer!);
                return completion.Task;
            });

        Assert.NotNull(completion);
        Assert.Same(completion.Task, returned);
        completion.CompleteSuccessfully();

        Assert.Equal("typed-result", await returned.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Single(capture.Stopped);
    }

    [Fact]
    public async Task TracedRequestCompletion_FaultedOperationCanceledException_PreservesFaultedTask()
    {
        using var capture = new ActivityCapture();
        using var cancellation = new CancellationTokenSource();
        var expected = new OperationCanceledException("fault", cancellation.Token);
        var activity = StartActivity();
        var observer = new CallCompletionObserver(activity, null);
        var completion = new TracedRequestCompletion(observer);
        Assert.True(observer.TryAttach(completion.Task));

        completion.CompleteWithError(expected);

        var actual = await Assert.ThrowsAsync<OperationCanceledException>(() => completion.Task);
        Assert.Same(expected, actual);
        Assert.True(completion.Task.IsFaulted);
        Assert.False(completion.Task.IsCanceled);
        Assert.Single(capture.Stopped);
    }

    [Fact]
    public async Task TracedRequestCompletion_CanceledCompletion_PreservesCancellationToken()
    {
        using var capture = new ActivityCapture();
        using var cancellation = new CancellationTokenSource();
        var activity = StartActivity();
        var observer = new CallCompletionObserver(activity, null);
        var completion = new TracedRequestCompletion(observer);
        Assert.True(observer.TryAttach(completion.Task));

        completion.CompleteCanceled(cancellation.Token);

        var actual = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => completion.Task);
        Assert.Equal(cancellation.Token, actual.CancellationToken);
        Assert.True(completion.Task.IsCanceled);
        Assert.Single(capture.Stopped);
    }

    [Fact]
    public async Task TracedBatchSubmissionCompletion_MultipleErrors_PublishesOnlyAfterFinalItem()
    {
        using var capture = new ActivityCapture();
        var first = new InvalidOperationException("first");
        var second = new ArgumentException("second");
        var activity = StartActivity();
        var observer = new CallCompletionObserver(activity, null);
        var completion = new TracedBatchSubmissionCompletion(3, default, observer);
        Assert.True(observer.TryAttach(completion.Task));

        completion.CompleteWithError(first);
        Assert.False(completion.Task.IsCompleted);
        Assert.Empty(capture.Stopped);

        completion.CompleteWithError(second);
        Assert.False(completion.Task.IsCompleted);
        Assert.Empty(capture.Stopped);

        completion.CompleteSuccessfully();
        var actual = await Assert.ThrowsAnyAsync<Exception>(() => completion.Task);
        Assert.True(completion.Task.IsFaulted);
        Assert.Equal(2, completion.Task.Exception!.InnerExceptions.Count);
        Assert.Contains(completion.Task.Exception.InnerExceptions, exception => ReferenceEquals(exception, first));
        Assert.Contains(completion.Task.Exception.InnerExceptions, exception => ReferenceEquals(exception, second));
        Assert.Same(first, actual);
        Assert.Single(capture.Stopped);
    }

    [Fact]
    public async Task TracedBatchSubmissionCompletion_CanceledFinalItem_PublishesCancellation()
    {
        using var capture = new ActivityCapture();
        using var cancellation = new CancellationTokenSource();
        var activity = StartActivity();
        var observer = new CallCompletionObserver(activity, null);
        var completion = new TracedBatchSubmissionCompletion(2, cancellation.Token, observer);
        Assert.True(observer.TryAttach(completion.Task));

        completion.CompleteCanceled();
        Assert.False(completion.Task.IsCompleted);
        completion.CompleteSuccessfully();

        var actual = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => completion.Task);
        Assert.Equal(cancellation.Token, actual.CancellationToken);
        Assert.True(completion.Task.IsCanceled);
        Assert.Single(capture.Stopped);
        Assert.Equal(true, Assert.Single(capture.Stopped).GetTagItem("requestbatcher.canceled"));
    }

    [Fact]
    public async Task RequestBatcherTracing_ObservedSubmissionFailure_StopsSpanAndRethrows()
    {
        using var capture = new ActivityCapture();
        var expected = new InvalidOperationException("submission failed");

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() => RequestBatcherTracing.TraceCall(
            new RequestBatcherMetadata("observed-failure"),
            expected,
            (exception, _, _) => throw exception));

        Assert.Same(expected, actual);
        var span = Assert.Single(capture.Stopped);
        Assert.Equal(ActivityStatusCode.Error, span.Status);
        Assert.Equal(nameof(InvalidOperationException), span.GetTagItem("error.type"));
    }

    [Fact]
    public async Task RequestBatcherTracing_GenericObservedSubmissionFailure_StopsSpanAndRethrows()
    {
        using var capture = new ActivityCapture();
        var expected = new InvalidOperationException("generic submission failed");

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() => RequestBatcherTracing.TraceCall<int, string>(
            new RequestBatcherMetadata("generic-observed-failure"),
            42,
            (value, _, _) => throw expected));

        Assert.Same(expected, actual);
        var span = Assert.Single(capture.Stopped);
        Assert.Equal(ActivityStatusCode.Error, span.Status);
        Assert.Equal(nameof(InvalidOperationException), span.GetTagItem("error.type"));
    }

    [Fact]
    public void RequestBatcherRequestType_GenericNonEntry_ReturnsSimpleTypeName()
    {
        Assert.Equal("List`1", RequestBatcherRequestType.GetName(typeof(List<int>)));
    }

    private static Activity StartActivity()
    {
        var activity = RequestBatcherTracing.Source.StartActivity("completion-test", ActivityKind.Internal);
        Assert.NotNull(activity);
        activity!.SetTag("test.marker", "submitter");
        return activity;
    }

    private sealed class ActivityCapture : IDisposable
    {
        private readonly ActivityListener _listener;
        private readonly Func<string?>? _marker;

        public ConcurrentQueue<Activity> Stopped { get; } = new();

        public ConcurrentQueue<bool> TaskWasCompletedAtStop { get; } = new();

        public Func<bool>? TaskCompletionState { get; set; }

        public ActivityCapture(Func<string?>? marker = null)
        {
            _marker = marker;
            _listener = new ActivityListener
            {
                ShouldListenTo = static source => source.Name == RequestBatcherTracing.SourceName,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = activity =>
                {
                    activity.SetTag("test.marker", _marker?.Invoke());
                    activity.SetTag("test.current", Activity.Current?.OperationName);
                    TaskWasCompletedAtStop.Enqueue(TaskCompletionState?.Invoke() ?? false);
                    Stopped.Enqueue(activity);
                },
            };
            ActivitySource.AddActivityListener(_listener);
        }

        public void Dispose() => _listener.Dispose();
    }
}
