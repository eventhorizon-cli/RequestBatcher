using System.Collections.Concurrent;
using System.Diagnostics;
using OpenTelemetry;
using OpenTelemetry.Trace;
using RequestBatcher.Diagnostics;
using RequestBatcher.PendingRequests;

namespace RequestBatcher.Tests.Diagnostics;

[Collection("Tracing")]
public sealed class LinkedBatchSamplingTests
{
    [Fact]
    public async Task AddRequestBatcherInstrumentation_Default_SamplesNamedBatchWhenAnyLinkIsSampled()
    {
        var sampler = new DecisionSampler(parameters =>
            parameters.Name.StartsWith(RequestBatcherTracing.CallName, StringComparison.Ordinal) &&
            parameters.Tags?.Any(tag => tag.Key == "requestbatcher.request.type" && Equals(tag.Value, "sampled")) == true
                ? _sampled
                : _dropped);
        using var capture = new CaptureProcessor();
        using var tracing = BuildTracer(sampler, capture);
        var sampled = StartSubmission("sampled");
        var unsampled = StartSubmission("unsampled");
        var sampledCompletion = new RequestCompletion();
        var unsampledCompletion = new RequestCompletion();
        var requests = new PendingBatchRequest<int>[]
        {
            NewRequest(sampled, sampledCompletion),
            NewRequest(unsampled, unsampledCompletion),
        };

        using var batch = RequestBatcherTracing.StartBatch("Int32", requests, "orders");

        Assert.NotNull(batch);
        Assert.True(batch!.Recorded);
        Assert.Contains(batch.Links, link => link.Context == sampled.Context);
        Assert.Contains(batch.Links, link => link.Context == unsampled.Context);
        Assert.DoesNotContain(sampler.Decisions, decision =>
            decision.Name == RequestBatcherTracing.BatchName + " orders");
        Assert.Equal(ActivityTraceFlags.Recorded, sampled.Context.TraceFlags);
        Assert.Equal(ActivityTraceFlags.None, unsampled.Context.TraceFlags);

        sampled.Completion.TrySetResult();
        unsampled.Completion.TrySetResult();
        await Task.WhenAll(sampled.Returned, unsampled.Returned);
    }

    [Fact]
    public async Task AddRequestBatcherInstrumentation_Default_DropsAllUnsampledNamedBatchEvenWithAlwaysOnRoot()
    {
        var sampler = new DecisionSampler(parameters =>
            parameters.Name.StartsWith(RequestBatcherTracing.CallName, StringComparison.Ordinal)
                ? _dropped
                : _sampled);
        using var capture = new CaptureProcessor();
        using var tracing = BuildTracer(sampler, capture);
        var first = StartSubmission("first");
        var second = StartSubmission("second");
        using var batch = RequestBatcherTracing.StartBatch(
            "Int32",
            [NewRequest(first, new RequestCompletion()), NewRequest(second, new RequestCompletion())],
            "orders");

        Assert.NotNull(batch);
        Assert.False(batch!.Recorded);
        Assert.Empty(batch.Links);
        Assert.DoesNotContain(sampler.Decisions, item =>
            item.Name == RequestBatcherTracing.BatchName + " orders");
        Assert.Empty(capture.Spans);

        first.Completion.TrySetResult();
        second.Completion.TrySetResult();
        await Task.WhenAll(first.Returned, second.Returned);
    }

    [Fact]
    public async Task AddRequestBatcherInstrumentation_Default_RecognizesStableNamedRootCreationShape()
    {
        var sampler = new DecisionSampler(_ => _dropped);
        using var capture = new CaptureProcessor();
        using var tracing = BuildTracer(sampler, capture);
        var startedBatches = new ConcurrentQueue<Activity>();
        using var listener = CreatePropagationCapture(startedBatches);
        var submission = StartSubmission("request");
        var batch = RequestBatcherTracing.StartBatch(
            "Int32", [NewRequest(submission, new RequestCompletion())], "pricing-queries");

        Assert.NotNull(batch);
        var started = Assert.Single(startedBatches);
        Assert.Equal(ActivityKind.Consumer, started.Kind);
        Assert.Equal(default, started.ParentSpanId);
        Assert.Equal("Int32", started.GetTagItem("requestbatcher.request.type"));
        Assert.Equal(1, started.GetTagItem("requestbatcher.batch.size"));
        Assert.Null(started.GetTagItem("requestbatcher.sample.linked"));
        Assert.DoesNotContain(sampler.Decisions, item =>
            item.Name == RequestBatcherTracing.BatchName + " pricing-queries");

        submission.Completion.TrySetResult();
        await submission.Returned;
        batch!.Dispose();
    }

    [Fact]
    public async Task AddRequestBatcherInstrumentation_Default_OriginalZeroRootStillSamplesBatchFromSampledCaller()
    {
        var sampler = new ParentBasedSampler(new TraceIdRatioBasedSampler(0));
        using var capture = new CaptureProcessor();
        using var tracing = BuildTracer(sampler, capture);
        using var parent = CreateParent(sampled: true);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        SubmissionTraceContext? context = null;
        var returned = RequestBatcherTracing.TraceCall(
            new RequestBatcherTraceMetadata("request"),
            completion,
            (state, traceContext) =>
            {
                context = traceContext;
                return state.Task;
            });
        var request = new TracedPendingBatchRequest<int>(
            1, default, static _ => { }, new RequestCompletion(), context!);

        // Match dispatch isolation so the batch cannot inherit the caller's sampled flag as a parent.
        Activity.Current = null;
        using var batch = RequestBatcherTracing.StartBatch("Int32", [request]);

        Assert.NotNull(batch);
        Assert.Equal(default, batch!.ParentSpanId);
        Assert.True(batch!.Recorded);

        completion.TrySetResult();
        await returned;
        batch!.Dispose();
        Assert.Contains(capture.Spans, span => span.OperationName == RequestBatcherTracing.CallName);
        Assert.Contains(capture.Spans, span => span.OperationName == RequestBatcherTracing.BatchName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AddRequestBatcherInstrumentation_Default_ParentBasedChildrenFollowBatchSampling(bool sampled)
    {
        var sampler = new ParentBasedSampler(new AlwaysOnSampler());
        using var capture = new CaptureProcessor();
        using var tracing = BuildTracer(sampler, capture, addApplicationSource: true);
        using var parent = CreateParent(sampled);
        var submission = StartSubmission("request");
        Activity.Current = null;
        var batch = RequestBatcherTracing.StartBatch(
            "Int32", [NewRequest(submission, new RequestCompletion())]);
        using var applicationSource = new ActivitySource("LinkedBatchSamplingTests.Application");
        var child = applicationSource.StartActivity("handler-child");

        Assert.NotNull(batch);
        Assert.Equal(default, batch!.ParentSpanId);
        Assert.Equal(sampled, batch.Recorded);
        Assert.Equal(sampled, child?.Recorded == true);
        if (sampled)
        {
            Assert.Equal(batch.SpanId, child!.ParentSpanId);
        }

        submission.Completion.TrySetResult();
        await submission.Returned;
        child?.Dispose();
        batch.Dispose();
        Assert.Equal(sampled, capture.Spans.Any(span => span.OperationName == "handler-child"));
        if (!sampled)
        {
            Assert.Empty(capture.Spans);
        }
    }

    [Fact]
    public void AddRequestBatcherInstrumentation_Default_DelegatesUnrelatedLookalikeWithoutCreationTags()
    {
        var sampler = new DecisionSampler(parameters =>
            parameters.Name.StartsWith(RequestBatcherTracing.CallName, StringComparison.Ordinal)
                ? _sampled
                : _dropped);
        using var capture = new CaptureProcessor();
        using var tracing = BuildTracer(sampler, capture, addApplicationSource: true);
        var source = new ActivitySource("LinkedBatchSamplingTests.Application");
        using var lookalike = source.StartActivity(
            RequestBatcherTracing.BatchName,
            ActivityKind.Consumer,
            parentContext: default);

        Assert.NotNull(lookalike);
        Assert.False(lookalike!.Recorded);
        var decision = Assert.Single(sampler.Decisions, item => item.Name == RequestBatcherTracing.BatchName);
        Assert.Equal(SamplingDecision.Drop, decision.Result.Decision);
    }

    [Fact]
    public async Task AddSource_DirectRegistration_PreservesOriginalBatchDecision()
    {
        var sampler = new DecisionSampler(parameters =>
            parameters.Name.StartsWith(RequestBatcherTracing.CallName, StringComparison.Ordinal)
                ? _sampled
                : _dropped);
        using var capture = new CaptureProcessor();
        using var tracing = BuildTracer(sampler, capture, useInstrumentation: false);
        var submission = StartSubmission("sampled");
        var batch = RequestBatcherTracing.StartBatch(
            "Int32", [NewRequest(submission, new RequestCompletion())]);

        Assert.NotNull(batch);
        Assert.False(batch!.Recorded);
        Assert.True(submission.Context.TraceFlags.HasFlag(ActivityTraceFlags.Recorded));
        var batchDecision = Assert.Single(sampler.Decisions, item =>
            item.Name == RequestBatcherTracing.BatchName);
        Assert.Equal(SamplingDecision.Drop, batchDecision.Result.Decision);

        submission.Completion.TrySetResult();
        await submission.Returned;
        batch!.Dispose();
        Assert.Contains(capture.Spans, span => span.OperationName == RequestBatcherTracing.CallName);
        Assert.DoesNotContain(capture.Spans, span => span.OperationName == RequestBatcherTracing.BatchName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AddRequestBatcherInstrumentation_Default_SetSamplerOrderStillDecorates(bool setBefore)
    {
        var sampler = new DecisionSampler(parameters =>
            parameters.Name.StartsWith(RequestBatcherTracing.CallName, StringComparison.Ordinal)
                ? _sampled
                : _dropped);
        using var capture = new CaptureProcessor();
        using var tracing = BuildTracer(
            sampler,
            capture,
            setSamplerBeforeInstrumentation: setBefore);
        var submission = StartSubmission("sampled");
        using var batch = RequestBatcherTracing.StartBatch(
            "Int32", [NewRequest(submission, new RequestCompletion())]);

        Assert.NotNull(batch);
        Assert.True(batch!.Recorded);
        Assert.DoesNotContain(sampler.Decisions, item =>
            item.Name == RequestBatcherTracing.BatchName);

        submission.Completion.TrySetResult();
        await submission.Returned;
    }

    [Fact]
    public void AddRequestBatcherInstrumentation_Default_RepeatedRegistrationDisposesOriginalOnce()
    {
        var sampler = new DecisionSampler(_ => _dropped);
        var builder = Sdk.CreateTracerProviderBuilder()
            .AddRequestBatcherInstrumentation()
            .AddRequestBatcherInstrumentation()
            .SetSampler(sampler);

        using (builder.Build())
        {
        }

        Assert.Equal(1, sampler.DisposeCount);
    }

    [Fact]
    public void AddRequestBatcherInstrumentation_Default_DisposedSamplerDropsAndDoesNotCallOriginal()
    {
        var original = new DecisionSampler(_ => _sampled);
        using var capture = new CaptureProcessor();
        var captured = BuildCapturedTracer(original, capture);
        using var provider = captured.Provider;
        var sampler = captured.Sampler;
        var parameters = new SamplingParameters(
            default,
            ActivityTraceId.CreateRandom(),
            RequestBatcherTracing.CallName,
            ActivityKind.Internal);

        Assert.NotSame(original, sampler);
        Assert.Equal(SamplingDecision.RecordAndSample, sampler.ShouldSample(parameters).Decision);
        Assert.Single(original.Decisions);

        provider.Dispose();

        Assert.Equal(SamplingDecision.Drop, sampler.ShouldSample(parameters).Decision);
        Assert.Single(original.Decisions);
        Assert.Equal(1, original.DisposeCount);

        provider.Dispose();
        Assert.IsAssignableFrom<IDisposable>(sampler).Dispose();
        Assert.Equal(1, original.DisposeCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AddRequestBatcherInstrumentation_NoSampledCreationLinks_DropsBatch(bool includeUnsampledLink)
    {
        var original = new DecisionSampler(_ => _sampled);
        using var capture = new CaptureProcessor();
        var captured = BuildCapturedTracer(original, capture);
        using var provider = captured.Provider;
        var sampler = captured.Sampler;
        var links = includeUnsampledLink
            ? [new ActivityLink(new ActivityContext(
                ActivityTraceId.CreateRandom(),
                ActivitySpanId.CreateRandom(),
                ActivityTraceFlags.None))]
            : Array.Empty<ActivityLink>();

        var result = sampler.ShouldSample(CreateBatchSamplingParameters(links, ValidBatchTags()));

        Assert.Equal(SamplingDecision.Drop, result.Decision);
        Assert.Empty(original.Decisions);
    }

    [Fact]
    public void AddRequestBatcherInstrumentation_ParentedSameNameConsumer_DelegatesToOriginalSampler()
    {
        var original = new DecisionSampler(_ => _sampled);
        using var capture = new CaptureProcessor();
        var captured = BuildCapturedTracer(original, capture);
        using var provider = captured.Provider;
        var parent = new ActivityContext(
            ActivityTraceId.CreateRandom(),
            ActivitySpanId.CreateRandom(),
            ActivityTraceFlags.Recorded);
        var sampler = captured.Sampler;

        var result = sampler.ShouldSample(CreateBatchSamplingParameters([], ValidBatchTags(), parent));

        Assert.Equal(SamplingDecision.RecordAndSample, result.Decision);
        Assert.Single(original.Decisions);
    }

    [Fact]
    public void AddRequestBatcherInstrumentation_IncompleteCreationTags_DelegatesToOriginalSampler()
    {
        var original = new DecisionSampler(_ => _sampled);
        using var capture = new CaptureProcessor();
        var captured = BuildCapturedTracer(original, capture);
        using var provider = captured.Provider;
        var sampler = captured.Sampler;

        var result = sampler.ShouldSample(CreateBatchSamplingParameters(
            [new ActivityLink(new ActivityContext(
                ActivityTraceId.CreateRandom(),
                ActivitySpanId.CreateRandom(),
                ActivityTraceFlags.None))],
            [new("requestbatcher.request.type", "Int32")]));

        Assert.Equal(SamplingDecision.RecordAndSample, result.Decision);
        Assert.Single(original.Decisions);
    }

    [Fact]
    public void AddRequestBatcherInstrumentation_InvalidCreationTags_DelegatesToOriginalSampler()
    {
        var original = new DecisionSampler(_ => _sampled);
        using var capture = new CaptureProcessor();
        var captured = BuildCapturedTracer(original, capture);
        using var provider = captured.Provider;

        var result = captured.Sampler.ShouldSample(CreateBatchSamplingParameters(
            [],
            [
                new("requestbatcher.batch.size", 0),
                new("requestbatcher.request.type", 42),
            ]));

        Assert.Equal(SamplingDecision.RecordAndSample, result.Decision);
        Assert.Single(original.Decisions);
    }

    private static TracedPendingBatchRequest<int> NewRequest(
        PendingSubmission submission,
        IRequestCompletion completion) =>
        new(0, default, static _ => { }, completion, submission.TraceContext);

    private static PendingSubmission StartSubmission(
        string requestType)
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

    private static TracerProvider BuildTracer(
        Sampler sampler,
        CaptureProcessor capture,
        bool useInstrumentation = true,
        bool setSamplerBeforeInstrumentation = false,
        bool addApplicationSource = false)
    {
        var builder = Sdk.CreateTracerProviderBuilder();
        if (addApplicationSource)
        {
            builder.AddSource("LinkedBatchSamplingTests.Application");
        }

        if (setSamplerBeforeInstrumentation)
        {
            builder.SetSampler(sampler);
        }

        if (useInstrumentation)
        {
            builder.AddRequestBatcherInstrumentation();
        }
        else
        {
            builder.AddSource(RequestBatcherTracing.SourceName);
        }

        if (!setSamplerBeforeInstrumentation)
        {
            builder.SetSampler(sampler);
        }

        return builder.AddProcessor(capture).Build();
    }

    private static (TracerProvider Provider, Sampler Sampler) BuildCapturedTracer(
        Sampler original,
        CaptureProcessor capture)
    {
        Sampler? captured = null;
        var provider = Sdk.CreateTracerProviderBuilder()
            .AddRequestBatcherInstrumentation()
            .ConfigureSampler((_, sampler) =>
            {
                captured = sampler;
                return sampler;
            })
            .SetSampler(original)
            .AddProcessor(capture)
            .Build();

        return (provider, captured ?? throw new InvalidOperationException("The SDK sampler was not captured."));
    }

    private static SamplingParameters CreateBatchSamplingParameters(
        IEnumerable<ActivityLink> links,
        IEnumerable<KeyValuePair<string, object?>> tags,
        ActivityContext parentContext = default) =>
        new(
            parentContext,
            ActivityTraceId.CreateRandom(),
            RequestBatcherTracing.BatchName,
            ActivityKind.Consumer,
            tags,
            links);

    private static KeyValuePair<string, object?>[] ValidBatchTags() =>
    [
        new("requestbatcher.batch.size", 1),
        new("requestbatcher.request.type", "Int32"),
    ];

    private static ActivityListener CreatePropagationCapture(ConcurrentQueue<Activity> batches)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = static source => source.Name == RequestBatcherTracing.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.PropagationData,
            ActivityStarted = activity =>
            {
                if (activity.OperationName.StartsWith(RequestBatcherTracing.BatchName, StringComparison.Ordinal))
                {
                    batches.Enqueue(activity);
                }
            },
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private static Activity CreateParent(bool sampled)
    {
        return new Activity("caller")
            .SetIdFormat(ActivityIdFormat.W3C)
            .SetParentId(
                ActivityTraceId.CreateRandom(),
                ActivitySpanId.CreateRandom(),
                sampled ? ActivityTraceFlags.Recorded : ActivityTraceFlags.None)
            .Start();
    }

    private static readonly SamplingResult _sampled = new(SamplingDecision.RecordAndSample);
    private static readonly SamplingResult _dropped = new(SamplingDecision.Drop);

    private sealed record PendingSubmission(
        Task Returned,
        TaskCompletionSource Completion,
        SubmissionTraceContext TraceContext)
    {
        public ActivityContext Context => TraceContext.Context;
    }

    private sealed class CaptureProcessor : BaseProcessor<Activity>
    {
        public ConcurrentQueue<Activity> Spans { get; } = new();

        public override void OnEnd(Activity data) => Spans.Enqueue(data);
    }

    private sealed class DecisionSampler(
        Func<SamplingParameters, SamplingResult> decide)
        : Sampler, IDisposable
    {
        public ConcurrentQueue<SamplingDecisionSnapshot> Decisions { get; } = new();
        public int DisposeCount => Volatile.Read(ref _disposeCount);
        private int _disposeCount;

        public override SamplingResult ShouldSample(in SamplingParameters samplingParameters)
        {
            var result = decide(samplingParameters);
            Decisions.Enqueue(new SamplingDecisionSnapshot(
                samplingParameters.Name,
                samplingParameters.Kind,
                samplingParameters.ParentContext,
                samplingParameters.Tags?.ToArray() ?? [],
                result));
            return result;
        }

        public void Dispose() => Interlocked.Increment(ref _disposeCount);
    }

    private sealed record SamplingDecisionSnapshot(
        string Name,
        ActivityKind Kind,
        ActivityContext ParentContext,
        KeyValuePair<string, object?>[] Tags,
        SamplingResult Result);
}
