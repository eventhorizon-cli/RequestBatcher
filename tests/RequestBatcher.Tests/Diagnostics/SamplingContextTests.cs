using System.Collections.Concurrent;
using System.Diagnostics;
using RequestBatcher.Diagnostics;
using RequestBatcher.PendingRequests;

namespace RequestBatcher.Tests.Diagnostics;

[Collection("Tracing")]
public sealed class SamplingContextTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TraceCall_PropagationData_EndsAfterPendingCompletionAndRestoresParent(bool useGeneric)
    {
        using var capture = new ActivityCapture(static options =>
            options.Name == RequestBatcherTracing.CallName
                ? ActivitySamplingResult.PropagationData
                : ActivitySamplingResult.AllDataAndRecorded);
        var previous = Activity.Current;
        using var parent = new Activity("parent").Start();
        var pending = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource? voidPending = null;

        try
        {
            Task returned;
            if (useGeneric)
            {
                returned = RequestBatcherTracing.TraceCall(
                    new RequestBatcherTraceMetadata("sampled-out"),
                    pending,
                    static (state, _) => state.Task);
            }
            else
            {
                returned = RequestBatcherTracing.TraceCall(
                    new RequestBatcherTraceMetadata("sampled-out"),
                    voidPending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
                    static (state, _) => state.Task);
                Assert.Same(parent, Activity.Current);
            }

            Assert.False(returned.IsCompleted);
            Assert.Same(parent, Activity.Current);
            Assert.Empty(capture.Stopped);

            if (useGeneric)
            {
                pending.SetResult(42);
            }
            else
            {
                voidPending!.SetResult();
            }

            await returned;

            Assert.Same(parent, Activity.Current);
            var stopped = Assert.Single(capture.Stopped);
            Assert.Equal(RequestBatcherTracing.CallName, stopped.OperationName);
            Assert.False(stopped.Recorded);
            Assert.Equal(parent.TraceId, stopped.TraceId);
            Assert.Equal(parent.SpanId, stopped.ParentSpanId);
        }
        finally
        {
            Activity.Current = previous;
        }
    }

    [Theory]
    [InlineData(ActivitySamplingResult.AllDataAndRecorded)]
    [InlineData(ActivitySamplingResult.AllData)]
    public async Task StartBatch_MixedSampling_DeduplicatesForwardLinksAndCollectsRecordedReverseLinks(
        ActivitySamplingResult dataSampling)
    {
        var batchSamplerLinks = new ConcurrentQueue<ActivityLink[]>();
        using var capture = new ActivityCapture(options =>
        {
            if (options.Name == RequestBatcherTracing.CallName)
            {
                var requestType = options.Tags?.Single(tag => tag.Key == "requestbatcher.request.type").Value;
                return Equals(requestType, "recorded")
                    ? dataSampling
                    : ActivitySamplingResult.PropagationData;
            }

            if (options.Name == RequestBatcherTracing.BatchName)
            {
                batchSamplerLinks.Enqueue(options.Links?.ToArray() ?? []);
            }

            return ActivitySamplingResult.AllDataAndRecorded;
        });
        var previous = Activity.Current;
        try
        {
            var recorded = StartPendingSubmission("recorded");
            var propagated = StartPendingSubmission("propagated");
            var requests = new PendingBatchRequest<int>[]
            {
                NewRequest(recorded.TraceContext),
                NewRequest(recorded.TraceContext),
                NewRequest(propagated.TraceContext),
                NewRequest(recorded.TraceContext),
                NewRequest(propagated.TraceContext),
            };

            var batch = RequestBatcherTracing.StartBatch("int", requests);
            Assert.NotNull(batch);
            using (batch)
            {
                var samplerLinks = Assert.Single(batchSamplerLinks);
                Assert.Equal(2, samplerLinks.Length);
                Assert.Contains(samplerLinks, link => link.Context == recorded.Context);
                Assert.Contains(samplerLinks, link => link.Context == propagated.Context);
                Assert.Equal(2, batch!.Links.Count());
            }

            recorded.Completion.SetResult();
            propagated.Completion.SetResult();
            await Task.WhenAll(recorded.Returned, propagated.Returned);

            var recordedSpan = Assert.Single(
                capture.Stopped,
                span => span.OperationName == RequestBatcherTracing.CallName &&
                    Equals(span.GetTagItem("requestbatcher.request.type"), "recorded"));
            var propagatedSpan = Assert.Single(
                capture.Stopped,
                span => span.OperationName == RequestBatcherTracing.CallName &&
                    Equals(span.GetTagItem("requestbatcher.request.type"), "propagated"));
            Assert.Equal(batch!.Context, Assert.Single(recordedSpan.Links).Context);
            Assert.Empty(propagatedSpan.Links);
        }
        finally
        {
            Activity.Current = previous;
        }
    }

    [Fact]
    public void TraceMetadata_ReusedForNamedCalls_ExposesReadOnlySamplingTags()
    {
        var sampledTags = new ConcurrentQueue<KeyValuePair<string, object?>[]>();
        var metadata = new RequestBatcherTraceMetadata("Order", "checkout");
        using var listener = new ActivityListener
        {
            ShouldListenTo = static source => source.Name == RequestBatcherTracing.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> options) =>
            {
                if (options.Name == metadata.CallName)
                {
                    sampledTags.Enqueue(options.Tags?.ToArray() ?? []);
                }

                return ActivitySamplingResult.AllDataAndRecorded;
            },
        };
        ActivitySource.AddActivityListener(listener);
        var tags = Assert.IsAssignableFrom<ICollection<KeyValuePair<string, object?>>>(metadata.Tags);

        Assert.True(tags.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => tags.Add(new("unexpected", true)));
        Assert.Equal(
            new[] { "requestbatcher.request.type", "requestbatcher.name" },
            metadata.Tags.Select(tag => tag.Key));

        var first = RequestBatcherTracing.TraceCall(
            metadata,
            Task.CompletedTask,
            static (state, _) => state);
        var second = RequestBatcherTracing.TraceCall(
            metadata,
            Task.CompletedTask,
            static (state, _) => state);

        Assert.True(first.IsCompletedSuccessfully);
        Assert.True(second.IsCompletedSuccessfully);
        Assert.Equal(2, sampledTags.Count);
        Assert.All(sampledTags, observed =>
        {
            Assert.Equal("Order", observed.Single(tag => tag.Key == "requestbatcher.request.type").Value);
            Assert.Equal("checkout", observed.Single(tag => tag.Key == "requestbatcher.name").Value);
        });
    }

    private static PendingBatchRequest<int> NewRequest(SubmissionTraceContext traceContext) =>
        new TracedPendingBatchRequest<int>(0, default, static _ => { }, null, traceContext);

    private static PendingSubmission StartPendingSubmission(string requestType)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        SubmissionTraceContext? context = null;
        var returned = RequestBatcherTracing.TraceCall(
            new RequestBatcherTraceMetadata(requestType),
            completion,
            (state, traceContext) =>
            {
                context = traceContext;
                return state.Task;
            });

        return new PendingSubmission(returned, completion, context!);
    }

    private sealed record PendingSubmission(
        Task Returned,
        TaskCompletionSource Completion,
        SubmissionTraceContext TraceContext)
    {
        public ActivityContext Context => TraceContext.Context;
    }

    private sealed class ActivityCapture : IDisposable
    {
        private readonly ActivityListener _listener;

        public ConcurrentQueue<Activity> Stopped { get; } = new();

        public ActivityCapture(
            Func<ActivityCreationOptions<ActivityContext>, ActivitySamplingResult> sample)
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = static source => source.Name == RequestBatcherTracing.SourceName,
                Sample = (ref ActivityCreationOptions<ActivityContext> options) => sample(options),
                ActivityStopped = Stopped.Enqueue,
            };
            ActivitySource.AddActivityListener(_listener);
        }

        public void Dispose() => _listener.Dispose();
    }
}
