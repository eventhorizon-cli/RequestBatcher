using System.Collections.Concurrent;
using System.Diagnostics;
using RequestBatcher.Diagnostics;

namespace RequestBatcher.Tests.Diagnostics;

[Collection("Tracing")]
public sealed class TraceCallCompletionTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void TraceCall_SubmitThrows_RestoresParentAndRecordsError(bool useGeneric, bool sampledOut)
    {
        using var capture = new ActivityCapture(
            sampledOut ? ActivitySamplingResult.None : ActivitySamplingResult.AllDataAndRecorded);
        var previous = Activity.Current;
        using var parent = new Activity("parent").Start();
        var expected = new InvalidOperationException("submission failed");

        try
        {
            void Invoke()
            {
                if (useGeneric)
                {
                    _ = RequestBatcherTracing.TraceCall<Exception, int>(
                        new RequestBatcherTraceMetadata("test"),
                        expected,
                        static (exception, _) => throw exception);
                }
                else
                {
                    _ = RequestBatcherTracing.TraceCall<Exception>(
                        new RequestBatcherTraceMetadata("test"),
                        expected,
                        static (exception, _) => throw exception);
                }
            }

            var actual = Assert.Throws<InvalidOperationException>(Invoke);

            Assert.Same(expected, actual);
            Assert.Same(parent, Activity.Current);
            if (sampledOut)
            {
                Assert.Empty(capture.Stopped);
            }
            else
            {
                var span = Assert.Single(capture.Stopped);
                Assert.Equal(RequestBatcherTracing.CallName, span.OperationName);
                Assert.Equal(ActivityStatusCode.Error, span.Status);
                Assert.Equal(typeof(InvalidOperationException).FullName, span.GetTagItem("error.type"));
            }
        }
        finally
        {
            Activity.Current = previous;
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task TraceCall_CompletedTasks_PreservesOutcomesAndEndsSpans(
        bool useGeneric,
        bool listenerDisabled)
    {
        using var capture = listenerDisabled ? null : new ActivityCapture();
        var previous = Activity.Current;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var expected = new InvalidOperationException("completed failure");

        try
        {
            Task success;
            Task faulted;
            Task canceled;
            Task returnedSuccess;
            Task returnedFault;
            Task returnedCanceled;
            if (useGeneric)
            {
                var genericSuccess = Task.FromResult(1);
                var genericFaulted = Task.FromException<int>(expected);
                var genericCanceled = Task.FromCanceled<int>(cancellation.Token);
                success = genericSuccess;
                faulted = genericFaulted;
                canceled = genericCanceled;
                returnedSuccess = InvokeGeneric("success", genericSuccess);
                returnedFault = InvokeGeneric("fault", genericFaulted);
                returnedCanceled = InvokeGeneric("canceled", genericCanceled);
            }
            else
            {
                var voidSuccess = Task.CompletedTask;
                var voidFaulted = Task.FromException(expected);
                var voidCanceled = Task.FromCanceled(cancellation.Token);
                success = voidSuccess;
                faulted = voidFaulted;
                canceled = voidCanceled;
                returnedSuccess = InvokeVoid("success", voidSuccess);
                returnedFault = InvokeVoid("fault", voidFaulted);
                returnedCanceled = InvokeVoid("canceled", voidCanceled);
            }

            Assert.Same(success, returnedSuccess);
            Assert.Same(faulted, returnedFault);
            Assert.Same(canceled, returnedCanceled);
            Assert.Same(expected, Assert.Single(faulted.Exception!.InnerExceptions));
            var canceledException = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => returnedCanceled);
            Assert.Equal(cancellation.Token, canceledException.CancellationToken);

            if (listenerDisabled)
            {
                Assert.Null(capture);
            }
            else
            {
                var spans = capture!.Stopped.ToArray();
                Assert.Equal(3, spans.Length);
                var successSpan = Assert.Single(
                    spans,
                    span => span.GetTagItem("requestbatcher.request.type") as string == "success");
                var faultSpan = Assert.Single(
                    spans,
                    span => span.GetTagItem("requestbatcher.request.type") as string == "fault");
                var canceledSpan = Assert.Single(
                    spans,
                    span => span.GetTagItem("requestbatcher.request.type") as string == "canceled");
                Assert.Equal(ActivityStatusCode.Unset, successSpan.Status);
                Assert.Equal(ActivityStatusCode.Error, faultSpan.Status);
                Assert.Equal(ActivityStatusCode.Unset, canceledSpan.Status);
                Assert.Equal(true, canceledSpan.GetTagItem("requestbatcher.canceled"));
            }
        }
        finally
        {
            Activity.Current = previous;
        }
    }

    [Fact]
    public async Task TraceCall_GenericPendingFaults_PreservesAggregateAndEndsErrorSpan()
    {
        using var capture = new ActivityCapture();
        var first = new InvalidOperationException("first");
        var canceled = new OperationCanceledException("faulted cancellation");
        var pending = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var returned = RequestBatcherTracing.TraceCall(
            new RequestBatcherTraceMetadata("pending-fault"),
            pending.Task,
            static (task, _) => task);

        Assert.False(returned.IsCompleted);
        pending.SetException([first, canceled]);

        var actual = await Assert.ThrowsAnyAsync<Exception>(() => returned);
        Assert.Same(first, actual);
        Assert.True(returned.IsFaulted);
        Assert.False(returned.IsCanceled);
        Assert.Equal(2, returned.Exception!.InnerExceptions.Count);
        Assert.Contains(returned.Exception.InnerExceptions, exception => ReferenceEquals(exception, first));
        Assert.Contains(returned.Exception.InnerExceptions, exception => ReferenceEquals(exception, canceled));
        var span = Assert.Single(capture.Stopped);
        Assert.Equal(ActivityStatusCode.Error, span.Status);
        Assert.Equal(typeof(InvalidOperationException).FullName, span.GetTagItem("error.type"));
    }

    [Fact]
    public async Task TraceCall_GenericPendingCancellation_PreservesTokenAndEndsSpan()
    {
        using var capture = new ActivityCapture();
        using var cancellation = new CancellationTokenSource();
        var pending = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var returned = RequestBatcherTracing.TraceCall(
            new RequestBatcherTraceMetadata("pending-canceled"),
            pending.Task,
            static (task, _) => task);

        Assert.False(returned.IsCompleted);
        pending.SetCanceled(cancellation.Token);

        var actual = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => returned);
        Assert.Equal(cancellation.Token, actual.CancellationToken);
        Assert.True(returned.IsCanceled);
        Assert.False(returned.IsFaulted);
        var span = Assert.Single(capture.Stopped);
        Assert.Equal(ActivityStatusCode.Unset, span.Status);
        Assert.Equal(true, span.GetTagItem("requestbatcher.canceled"));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public void TraceCall_SampledOut_ReturnsOriginalTaskAndPassesFallbackContext(
        bool useGeneric,
        bool hasParent)
    {
        using var capture = new ActivityCapture(ActivitySamplingResult.None);
        var previous = Activity.Current;
        Activity? parent = null;
        try
        {
            Activity.Current = null;
            if (hasParent)
            {
                parent = new Activity("parent").Start();
            }

            ActivityContext passed = default;
            Task original;
            Task returned;
            if (useGeneric)
            {
                var genericOriginal = Task.FromResult(42);
                original = genericOriginal;
                returned = InvokeGeneric(
                    "sampled-out",
                    genericOriginal,
                    context => passed = context);
            }
            else
            {
                var voidOriginal = Task.CompletedTask;
                original = voidOriginal;
                returned = InvokeVoid(
                    "sampled-out",
                    voidOriginal,
                    context => passed = context);
            }

            Assert.Same(original, returned);
            Assert.Equal(hasParent ? parent!.Context : default, passed);
            Assert.Same(parent, Activity.Current);
            Assert.Empty(capture.Stopped);
        }
        finally
        {
            parent?.Dispose();
            Activity.Current = previous;
        }
    }

    private sealed class ActivityCapture : IDisposable
    {
        private readonly ActivityListener _listener;

        public ConcurrentQueue<Activity> Stopped { get; } = new();

        public ActivityCapture(ActivitySamplingResult samplingResult = ActivitySamplingResult.AllDataAndRecorded)
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = static source => source.Name == RequestBatcherTracing.SourceName,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => samplingResult,
                ActivityStopped = Stopped.Enqueue,
            };
            ActivitySource.AddActivityListener(_listener);
        }

        public void Dispose() => _listener.Dispose();
    }

    private static Task InvokeVoid(
        string requestTypeName,
        Task task,
        Action<ActivityContext>? captureContext = null)
    {
        return RequestBatcherTracing.TraceCall(
            new RequestBatcherTraceMetadata(requestTypeName),
            task,
            (state, context) =>
            {
                captureContext?.Invoke(context?.Context ?? default);
                return state;
            });
    }

    private static Task<TResult> InvokeGeneric<TResult>(
        string requestTypeName,
        Task<TResult> task,
        Action<ActivityContext>? captureContext = null)
    {
        return RequestBatcherTracing.TraceCall(
            new RequestBatcherTraceMetadata(requestTypeName),
            task,
            (state, context) =>
            {
                captureContext?.Invoke(context?.Context ?? default);
                return state;
            });
    }
}
