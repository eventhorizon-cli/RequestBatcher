using System.Collections.Concurrent;
using System.Diagnostics;
using RequestBatcher.Diagnostics;
using RequestBatcher.PendingRequests;

namespace RequestBatcher.Tests.Diagnostics;

[Collection("Tracing")]
public sealed class TracingSamplingContractTests
{
    [Theory]
    [InlineData(ActivitySamplingResult.PropagationData)]
    [InlineData(ActivitySamplingResult.AllData)]
    public async Task StartBatch_AllUnsampledCalls_OmitsForwardAndReverseLinks(ActivitySamplingResult callSampling)
    {
        using var capture = new SamplingCapture(options => options.Name == RequestBatcherTracing.CallName
            ? callSampling
            : ActivitySamplingResult.AllDataAndRecorded);
        var submission = StartPendingSubmission("unsampled");
        var requests = new[] { NewRequest(submission, new RequestCompletion()) };

        using var batch = RequestBatcherTracing.StartBatch("int", requests);

        Assert.NotNull(batch);
        Assert.Empty(capture.BatchCreationLinks.Single());
        Assert.Empty(batch!.Links);
        Assert.Equal(1, Assert.IsType<int>(batch.GetTagItem("requestbatcher.batch.submission.count")));

        submission.Completion.TrySetResult();
        await submission.Returned;
        var call = Assert.Single(capture.Calls);
        Assert.Empty(call.Links);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StartBatch_MixedSampling_PreservesAllContextsAndDeduplicatesLinks(bool sampledFirst)
    {
        using var capture = new SamplingCapture(options => options.Name == RequestBatcherTracing.CallName
            ? Equals(options.Tags?.Single(tag => tag.Key == "requestbatcher.request.type").Value, "sampled")
                ? ActivitySamplingResult.AllDataAndRecorded
                : ActivitySamplingResult.PropagationData
            : ActivitySamplingResult.AllDataAndRecorded);
        var sampled = StartPendingSubmission("sampled");
        var unsampled = StartPendingSubmission("unsampled");
        var sampledCompletion = new RequestCompletion();
        var unsampledCompletion = new RequestCompletion();
        var first = sampledFirst ? sampled : unsampled;
        var second = sampledFirst ? unsampled : sampled;
        var requests = new[]
        {
            NewRequest(first, sampledFirst ? sampledCompletion : unsampledCompletion),
            NewRequest(first, sampledFirst ? sampledCompletion : unsampledCompletion),
            NewRequest(second, sampledFirst ? unsampledCompletion : sampledCompletion),
            NewRequest(first, sampledFirst ? sampledCompletion : unsampledCompletion),
        };

        using var batch = RequestBatcherTracing.StartBatch("int", requests);

        Assert.NotNull(batch);
        var creationLinks = Assert.Single(capture.BatchCreationLinks);
        Assert.Equal(2, creationLinks.Length);
        Assert.Contains(creationLinks, link => link.Context == sampled.Context);
        Assert.Contains(creationLinks, link => link.Context == unsampled.Context);
        Assert.DoesNotContain(capture.BatchCreationTags.Single(), tag =>
            tag.Key == "requestbatcher.batch.submission.count");
        Assert.Equal(2, batch!.Links.Count());
        Assert.Contains(batch.Links, link => link.Context == sampled.Context);
        Assert.Contains(batch.Links, link => link.Context == unsampled.Context);
        Assert.Equal(2, Assert.IsType<int>(batch.GetTagItem("requestbatcher.batch.submission.count")));

        sampled.Completion.TrySetResult();
        unsampled.Completion.TrySetResult();
        await Task.WhenAll(sampled.Returned, unsampled.Returned);
        var sampledCall = Assert.Single(capture.Calls, call => call.Context == sampled.Context);
        var unsampledCall = Assert.Single(capture.Calls, call => call.Context == unsampled.Context);
        Assert.Equal(batch.Context, Assert.Single(sampledCall.Links).Context);
        Assert.Empty(unsampledCall.Links);
    }

    [Fact]
    public async Task StartBatch_RecordOnlyCall_DoesNotCollectReverseLink()
    {
        using var capture = new SamplingCapture(options => options.Name == RequestBatcherTracing.CallName
            ? ActivitySamplingResult.AllData
            : ActivitySamplingResult.AllDataAndRecorded);
        var submission = StartPendingSubmission("record-only");
        using var batch = RequestBatcherTracing.StartBatch(
            "int", [NewRequest(submission, new RequestCompletion())]);

        Assert.NotNull(batch);
        Assert.Empty(batch!.Links);
        submission.Completion.TrySetResult();
        await submission.Returned;
        Assert.Empty(Assert.Single(capture.Calls).Links);
    }

    [Fact]
    public async Task StartBatch_SubmissionCount_DeduplicatesExplicitItemsAndSeparatesSameParentCalls()
    {
        using var capture = new SamplingCapture(_ => ActivitySamplingResult.AllDataAndRecorded);
        using var parent = new Activity("caller").Start();
        var first = StartPendingSubmission("first");
        var second = StartPendingSubmission("second");
        var firstCompletion = new RequestCompletion();
        var secondCompletion = new RequestCompletion();
        var requests = new PendingBatchRequest<int>[]
        {
            NewRequest(first, firstCompletion),
            NewRequest(first, firstCompletion),
            NewRequest(second, secondCompletion),
            new(4, default, static _ => { }),
        };

        using var batch = RequestBatcherTracing.StartBatch("int", requests);

        Assert.NotNull(batch);
        Assert.Equal(3, Assert.IsType<int>(batch!.GetTagItem("requestbatcher.batch.submission.count")));
        Assert.Equal(2, batch.Links.Count());
        Assert.DoesNotContain(capture.BatchCreationTags.Single(), tag =>
            tag.Key == "requestbatcher.batch.submission.count");

        first.Completion.TrySetResult();
        second.Completion.TrySetResult();
        await Task.WhenAll(first.Returned, second.Returned);
    }

    [Fact]
    public void StartBatch_EmptyInput_RecordsZeroSubmissionsAndNoLinks()
    {
        using var capture = new SamplingCapture(_ => ActivitySamplingResult.AllDataAndRecorded);

        using var batch = RequestBatcherTracing.StartBatch<int>("int", []);

        Assert.NotNull(batch);
        Assert.Empty(batch!.Links);
        Assert.Equal(0, Assert.IsType<int>(batch.GetTagItem("requestbatcher.batch.size")));
        Assert.Equal(0, Assert.IsType<int>(batch.GetTagItem("requestbatcher.batch.submission.count")));
    }

    [Fact]
    public void StartBatch_UntracedRequests_CountsSharedAndSeparateCompletions()
    {
        using var capture = new SamplingCapture(_ => ActivitySamplingResult.AllDataAndRecorded);
        using var parent = new Activity("caller").Start();
        var sharedCompletion = new BatchSubmissionCompletion(2, default);
        var requests = new PendingBatchRequest<int>[]
        {
            new(1, default, static _ => { }, sharedCompletion),
            new(2, default, static _ => { }, sharedCompletion),
            new(3, default, static _ => { }, new RequestCompletion()),
            new(4, default, static _ => { }, new RequestCompletion()),
        };

        using var batch = RequestBatcherTracing.StartBatch("int", requests);

        Assert.NotNull(batch);
        Assert.Empty(batch!.Links);
        Assert.Equal(3, Assert.IsType<int>(batch.GetTagItem("requestbatcher.batch.submission.count")));
        Assert.DoesNotContain(capture.BatchCreationTags.Single(), tag =>
            tag.Key == "requestbatcher.batch.submission.count");
    }

    private static TracedPendingBatchRequest<int> NewRequest(
        PendingSubmission submission,
        IRequestCompletion completion) =>
        new(0, default, static _ => { }, completion, submission.TraceContext);

    private static PendingSubmission StartPendingSubmission(string requestType)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        SubmissionTraceContext? traceContext = null;
        var returned = RequestBatcherTracing.TraceCall(
            new RequestBatcherTraceMetadata(requestType),
            completion,
            (state, context) =>
            {
                traceContext = context;
                return state.Task;
            });

        return new PendingSubmission(returned, completion, traceContext!);
    }

    private sealed record PendingSubmission(
        Task Returned,
        TaskCompletionSource Completion,
        SubmissionTraceContext TraceContext)
    {
        public ActivityContext Context => TraceContext.Context;
    }

    private sealed class SamplingCapture : IDisposable
    {
        private readonly ActivityListener _listener;
        private readonly Func<ActivityCreationOptions<ActivityContext>, ActivitySamplingResult> _sample;

        public ConcurrentQueue<Activity> Stopped { get; } = new();
        public ConcurrentQueue<ActivityLink[]> BatchCreationLinks { get; } = new();
        public ConcurrentQueue<KeyValuePair<string, object?>[]> BatchCreationTags { get; } = new();
        public IEnumerable<Activity> Calls => Stopped.Where(span =>
            span.OperationName == RequestBatcherTracing.CallName);

        public SamplingCapture(Func<ActivityCreationOptions<ActivityContext>, ActivitySamplingResult> sample)
        {
            _sample = sample;
            _listener = new ActivityListener
            {
                ShouldListenTo = static source => source.Name == RequestBatcherTracing.SourceName,
                Sample = (ref ActivityCreationOptions<ActivityContext> options) =>
                {
                    if (options.Name == RequestBatcherTracing.BatchName)
                    {
                        BatchCreationLinks.Enqueue(options.Links?.ToArray() ?? []);
                        BatchCreationTags.Enqueue(options.Tags?.ToArray() ?? []);
                    }

                    return _sample(options);
                },
                ActivityStopped = Stopped.Enqueue,
            };
            ActivitySource.AddActivityListener(_listener);
        }

        public void Dispose() => _listener.Dispose();
    }
}
