using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Trace;
using Xunit;
using Xunit.Abstractions;

namespace RequestBatcher.Observability.IntegrationTests;

/// <summary>
/// Exercises the public batcher API with the real OpenTelemetry SDK without depending on Docker.
/// </summary>
public sealed class TracingStressTests(ITestOutputHelper output)
{
    private static readonly TimeSpan _testTimeout = TimeSpan.FromSeconds(60);
    private const string ExecuteOperation = "RequestBatcher.Execute";
    private const string BatchOperation = "RequestBatcher.Handle";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_ConcurrentWavesAtEachSamplingRate_ProcessesExactlyOnceAndLinksActualBatches(
        bool sample)
    {
        using var timeout = new CancellationTokenSource(_testTimeout);
        using var capture = new SpanCapture();
        using var tracing = Sdk.CreateTracerProviderBuilder()
            .AddRequestBatcherInstrumentation()
            .SetSampler(sample ? new AlwaysOnSampler() : new AlwaysOffSampler())
            .AddProcessor(capture)
            .Build();
        var observations = new HandlerObservations();
        var provider = CreateProvider(observations, batchSize: 8, maxConcurrency: 8, maxPending: 64);
        var batcher = provider.GetRequiredService<IRequestBatcher<int>>();

        var nextRequest = 1;
        try
        {
            for (var wave = 0; wave < 32; wave++)
            {
                var submissions = new List<Submission>();
                var waveRequestCount = 0;
                var submissionIndex = 0;
                while (waveRequestCount < 512)
                {
                    var count = waveRequestCount + 16 <= 512 && submissionIndex % 3 != 0
                        ? 16
                        : 1;
                    var requests = Enumerable.Range(nextRequest, count).ToArray();
                    nextRequest += count;
                    waveRequestCount += count;
                    submissions.Add(new Submission(wave, submissionIndex++, requests));
                }

                await Task.WhenAll(submissions.Select(submission =>
                    SubmitAsync(batcher, submission, timeout.Token))).WaitAsync(timeout.Token);

                AssertWave(submissions, observations, capture, sample);
                output.WriteLine($"sample={sample}, wave={wave + 1}/32: 512 requests, " +
                                 $"{submissions.Count} submissions, {observations.Batches.Count} handler batches.");
                capture.Clear();
                observations.Clear();
            }
        }
        finally
        {
            try
            {
                await ((IAsyncDisposable)batcher).DisposeAsync().AsTask().WaitAsync(_testTimeout);
            }
            finally
            {
                await provider.DisposeAsync().AsTask().WaitAsync(_testTimeout);
            }
        }
    }

    [Fact]
    public async Task ExecuteAsync_Instrumentation_MixedSamplingLinksAllParticipatingContexts()
    {
        using var timeout = new CancellationTokenSource(_testTimeout);
        using var capture = new SpanCapture();
        using var tracing = Sdk.CreateTracerProviderBuilder()
            .AddRequestBatcherInstrumentation()
            .SetSampler(new ParentBasedSampler(new AlwaysOnSampler()))
            .AddProcessor(capture)
            .Build();
        var executeActivities = new ConcurrentBag<ExecuteObservation>();
        using var propagationListener = new ActivityListener
        {
            ShouldListenTo = static source => source.Name == "RequestBatcher",
            Sample = (ref ActivityCreationOptions<ActivityContext> options) =>
                options.Name == ExecuteOperation
                    ? ActivitySamplingResult.PropagationData
                    : ActivitySamplingResult.None,
            ActivityStarted = activity =>
            {
                if (activity.OperationName == ExecuteOperation)
                {
                    executeActivities.Add(new ExecuteObservation(activity.Context, activity.ParentSpanId));
                }
            },
        };
        ActivitySource.AddActivityListener(propagationListener);
        var services = new ServiceCollection();
        services.AddRequestBatcher<int, MixedSamplingHandler>(ServiceLifetime.Singleton, options =>
        {
            options.BatchSize = 64;
            options.MaxConcurrency = 1;
            options.MaxPendingRequests = 128;
            options.FullMode = RequestBatchFullMode.Wait;
        });

        var provider = services.BuildServiceProvider();
        var handler = Assert.IsType<MixedSamplingHandler>(provider.GetRequiredService<IRequestBatchHandler<int>>());
        var batcher = provider.GetRequiredService<IRequestBatcher<int>>();
        var submissions = Enumerable.Range(0, 32)
            .Select(request => new MixedSubmission(request, request % 2 == 0))
            .ToArray();
        var allSubmitted = NewSource();
        var submitted = 0;

        try
        {
            async Task SubmitAsync(MixedSubmission submission)
            {
                using var parent = CreateCallerActivity(submission.Sampled, submission.Request);
                submission.ParentContext = parent.Context;
                var task = batcher.ExecuteAsync(submission.Request);
                if (Interlocked.Increment(ref submitted) == submissions.Length)
                {
                    allSubmitted.TrySetResult();
                }

                await task.WaitAsync(timeout.Token);
            }

            var first = SubmitAsync(submissions[0]);
            await handler.Started.Task.WaitAsync(timeout.Token);
            var queued = submissions.Skip(1).Select(SubmitAsync).ToArray();
            await allSubmitted.Task.WaitAsync(timeout.Token);
            handler.Release.TrySetResult();
            await Task.WhenAll(queued.Append(first)).WaitAsync(timeout.Token);

            var executeByParent = executeActivities
                .GroupBy(activity => activity.ParentSpanId)
                .ToDictionary(group => group.Key, group => Assert.Single(group).Context);
            Assert.Equal(submissions.Length, executeByParent.Count);
            var executeSpans = capture.Spans
                .Where(span => span.OperationName == ExecuteOperation)
                .ToArray();
            var recordedExecuteContexts = executeByParent.Values
                .Where(context => context.TraceFlags.HasFlag(ActivityTraceFlags.Recorded))
                .ToHashSet();
            Assert.True(recordedExecuteContexts.SetEquals(executeSpans.Select(span => span.Context)));
            Assert.DoesNotContain(executeSpans, span =>
                !span.Context.TraceFlags.HasFlag(ActivityTraceFlags.Recorded));

            var batchSpans = capture.Spans
                .Where(span => span.OperationName == BatchOperation)
                .ToArray();
            Assert.NotEmpty(batchSpans);
            var executeByRequest = submissions.ToDictionary(
                submission => submission.Request,
                submission => executeByParent[submission.ParentContext.SpanId]);
            var mixedBatchFound = false;
            foreach (var batchRequests in handler.Batches)
            {
                var batchContext = Assert.Single(handler.BatchContexts, context => context.Requests == batchRequests).Context;
                var hasSampledParticipant = batchRequests.Any(request =>
                    executeByRequest[request].TraceFlags.HasFlag(ActivityTraceFlags.Recorded));
                Assert.Equal(hasSampledParticipant, batchContext.TraceFlags.HasFlag(ActivityTraceFlags.Recorded));
                if (!hasSampledParticipant)
                {
                    Assert.DoesNotContain(batchSpans, span => span.Context == batchContext);
                    continue;
                }

                var batchSpan = Assert.Single(batchSpans, span => span.Context == batchContext);
                var expectedLinks = batchRequests.Select(request => executeByRequest[request]).ToHashSet();
                Assert.True(expectedLinks.SetEquals(batchSpan.Links.Select(link => link.Context)));
                Assert.Equal(batchRequests.Distinct().Count(), batchSpan.SubmissionCount);

                var hasUnsampledParticipant = batchRequests.Any(request =>
                    !executeByRequest[request].TraceFlags.HasFlag(ActivityTraceFlags.Recorded));
                if (hasSampledParticipant && hasUnsampledParticipant)
                {
                    mixedBatchFound = true;
                    Assert.Contains(batchSpan.Links, link =>
                        batchRequests.Any(request => executeByRequest[request] == link.Context &&
                            executeByRequest[request].TraceFlags.HasFlag(ActivityTraceFlags.Recorded)));
                    Assert.Contains(batchSpan.Links, link =>
                        batchRequests.Any(request => executeByRequest[request] == link.Context &&
                            !executeByRequest[request].TraceFlags.HasFlag(ActivityTraceFlags.Recorded)));
                }
            }

            Assert.Equal(handler.BatchContexts.Count(context => context.Context.TraceFlags.HasFlag(ActivityTraceFlags.Recorded)),
                batchSpans.Length);
            Assert.True(mixedBatchFound, "Expected the SDK sampler to observe a batch with sampled and unsampled submissions.");
        }
        finally
        {
            handler.Release.TrySetResult();
            using var cleanupTimeout = new CancellationTokenSource(_testTimeout);
            try
            {
                await ((IAsyncDisposable)batcher).DisposeAsync().AsTask().WaitAsync(cleanupTimeout.Token);
            }
            finally
            {
                await provider.DisposeAsync().AsTask().WaitAsync(_testTimeout);
            }
        }
    }

    [Fact]
    public async Task ExecuteAsync_Instrumentation_SamplesBatchWithZeroRootRatio()
    {
        using var timeout = new CancellationTokenSource(_testTimeout);
        using var capture = new SpanCapture();
        using var tracing = Sdk.CreateTracerProviderBuilder()
            .AddRequestBatcherInstrumentation()
            .SetSampler(new ParentBasedSampler(new TraceIdRatioBasedSampler(0)))
            .AddProcessor(capture)
            .Build();
        var observations = new HandlerObservations();
        var provider = CreateProvider(observations, batchSize: 8, maxConcurrency: 1, maxPending: 8);
        var batcher = provider.GetRequiredService<IRequestBatcher<int>>();

        try
        {
            using var parent = CreateCallerActivity(sampled: true, request: 0);
            await batcher.ExecuteAsync(1).WaitAsync(timeout.Token);

            var call = Assert.Single(capture.Spans, span => span.OperationName == ExecuteOperation);
            var batch = Assert.Single(capture.Spans, span => span.OperationName == BatchOperation);
            Assert.Equal(parent.SpanId, call.ParentSpanId);
            Assert.Contains(batch.Links, link => link.Context == call.Context);
            Assert.True(batch.Context.TraceFlags.HasFlag(ActivityTraceFlags.Recorded));
        }
        finally
        {
            try
            {
                await ((IAsyncDisposable)batcher).DisposeAsync().AsTask().WaitAsync(_testTimeout);
            }
            finally
            {
                await provider.DisposeAsync().AsTask().WaitAsync(_testTimeout);
            }
        }
    }

    private static void AssertWave(
        IReadOnlyList<Submission> submissions,
        HandlerObservations observations,
        SpanCapture capture,
        bool sample)
    {
        var expectedRequests = submissions.SelectMany(submission => submission.Requests).ToHashSet();
        Assert.Equal(expectedRequests.Count, observations.ProcessedRequests.Count);
        Assert.All(expectedRequests, request =>
            Assert.Equal(1, observations.ProcessedRequests[request]));
        Assert.NotEmpty(observations.Batches);
        Assert.True(observations.Batches.Count > 4, $"Expected several handler batches, got {observations.Batches.Count}.");

        if (!sample)
        {
            Assert.Empty(capture.Spans);
            return;
        }

        var expectedByParent = submissions.ToDictionary(
            submission => submission.ParentContext.SpanId,
            submission => submission);
        var submissionByRequest = submissions
            .SelectMany(submission => submission.Requests.Select(request => (request, submission)))
            .ToDictionary(item => item.request, item => item.submission);
        var executeSpans = capture.Spans
            .Where(span => span.OperationName == ExecuteOperation && expectedByParent.ContainsKey(span.ParentSpanId))
            .ToArray();
        Assert.Equal(submissions.Count, executeSpans.Length);

        var executeBySubmission = executeSpans.ToDictionary(
            span => expectedByParent[span.ParentSpanId],
            span => span);
        var batchSpans = capture.Spans.Where(span => span.OperationName == BatchOperation).ToArray();
        Assert.Equal(observations.Batches.Count, batchSpans.Length);

        var batchByRequest = observations.Batches
            .SelectMany(batch => batch.Requests.Select(request => (request, batch)))
            .ToDictionary(item => item.request, item => item.batch);
        foreach (var observation in observations.Batches)
        {
            var span = Assert.Single(batchSpans, candidate => candidate.Context == observation.Context);
            Assert.Equal(default, span.ParentSpanId);
            Assert.Single(observation.Requests
                .Select(request => submissionByRequest[request].Wave)
                .Distinct());
            var expectedCallContexts = observation.Requests
                .Select(request => executeBySubmission[submissionByRequest[request]].Context)
                .ToHashSet();
            Assert.True(expectedCallContexts.SetEquals(span.Links.Select(link => link.Context)),
                $"Batch {span.Context.TraceId}/{span.Context.SpanId} links do not match its request membership.");
            Assert.Equal(expectedCallContexts.Count, span.Links.Length);
            Assert.DoesNotContain(span.Links, link => submissions.Any(submission =>
                link.Context == submission.ParentContext));
        }

        foreach (var submission in submissions)
        {
            var execute = executeBySubmission[submission];
            var expectedBatchContexts = submission.Requests
                .Select(request => batchByRequest[request].Context)
                .ToHashSet();
            Assert.True(expectedBatchContexts.SetEquals(execute.Links.Select(link => link.Context)),
                $"Execute {execute.Context.TraceId}/{execute.Context.SpanId} links do not match its batch membership.");
            Assert.Equal(expectedBatchContexts.Count, execute.Links.Length);
            Assert.DoesNotContain(execute.Links, link => link.Context == submission.ParentContext);
        }
    }

    [Fact]
    public async Task DisposeAsync_QueuedCancellationAndHandlerFailure_DrainsAcceptedWorkAndRejectsLateSubmissions()
    {
        using var timeout = new CancellationTokenSource(_testTimeout);
        using var capture = new SpanCapture();
        using var tracing = Sdk.CreateTracerProviderBuilder()
            .AddRequestBatcherInstrumentation()
            .SetSampler(new AlwaysOnSampler())
            .AddProcessor(capture)
            .Build();
        var services = new ServiceCollection();
        services.AddRequestBatcher<int, ControlledHandler>(ServiceLifetime.Singleton, options =>
        {
            options.BatchSize = 1;
            options.MaxConcurrency = 1;
            options.MaxPendingRequests = 8;
            options.FullMode = RequestBatchFullMode.Wait;
        });

        var provider = services.BuildServiceProvider();
        var handler = (ControlledHandler)provider.GetRequiredService<IRequestBatchHandler<int>>();
        var batcher = provider.GetRequiredService<IRequestBatcher<int>>();
        try
        {
            var first = batcher.ExecuteAsync(1);
            await handler.Started.Task.WaitAsync(timeout.Token);

            using var canceled = new CancellationTokenSource();
            var canceledTask = batcher.ExecuteAsync(2, canceled.Token);
            var successfulTask = batcher.ExecuteAsync(3);
            var failingTask = batcher.ExecuteAsync(4);
            canceled.Cancel();

            var disposeTask = ((IAsyncDisposable)batcher).DisposeAsync().AsTask();
            await Assert.ThrowsAsync<ObjectDisposedException>(() => batcher.ExecuteAsync(99));

            handler.Release.TrySetResult();
            await first.WaitAsync(timeout.Token);
            await successfulTask.WaitAsync(timeout.Token);
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => failingTask.WaitAsync(timeout.Token));
            Assert.Same(handler.Failure, failure);
            var cancellation = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledTask.WaitAsync(timeout.Token));
            Assert.Equal(canceled.Token, cancellation.CancellationToken);
            await disposeTask.WaitAsync(timeout.Token);

            Assert.Equal(new[] { 1, 3, 4 }, handler.ProcessedRequests.OrderBy(request => request));
            var executeSpans = capture.Spans.Where(span => span.OperationName == ExecuteOperation).ToArray();
            Assert.True(executeSpans.Length >= 4, $"Expected traced calls for cancellation, failure, and disposal; got {executeSpans.Length}.");
            Assert.Contains(executeSpans, span => span.Canceled);
            Assert.Contains(executeSpans, span => span.Status == ActivityStatusCode.Error);
            Assert.Contains(capture.Spans, span => span.OperationName == BatchOperation);
            output.WriteLine($"drain processed {handler.ProcessedRequests.Count} requests; queued cancellation was omitted.");
        }
        finally
        {
            handler.Release.TrySetResult();
            try
            {
                await ((IAsyncDisposable)batcher).DisposeAsync().AsTask().WaitAsync(_testTimeout);
            }
            finally
            {
                await provider.DisposeAsync().AsTask().WaitAsync(_testTimeout);
            }
        }
    }

    private static ServiceProvider CreateProvider(
        HandlerObservations observations,
        int batchSize,
        int maxConcurrency,
        int maxPending)
    {
        var services = new ServiceCollection();
        services.AddSingleton(observations);
        services.AddRequestBatcher<int, ObservingHandler>(ServiceLifetime.Singleton, options =>
        {
            options.BatchSize = batchSize;
            options.MaxConcurrency = maxConcurrency;
            options.MaxPendingRequests = maxPending;
            options.FullMode = RequestBatchFullMode.Wait;
        });
        return services.BuildServiceProvider();
    }

    private static async Task SubmitAsync(
        IRequestBatcher<int> batcher,
        Submission submission,
        CancellationToken cancellationToken)
    {
        using var parent = new Activity($"caller-{submission.Wave}-{submission.Index}")
            .SetIdFormat(ActivityIdFormat.W3C)
            .Start();
        submission.ParentContext = parent.Context;
        if (submission.Requests.Length == 1)
        {
            await batcher.ExecuteAsync(submission.Requests[0], cancellationToken).WaitAsync(cancellationToken);
        }
        else
        {
            await batcher.ExecuteAsync(submission.Requests, cancellationToken).WaitAsync(cancellationToken);
        }
    }

    private static Activity CreateCallerActivity(bool sampled, int request)
    {
        var activity = new Activity($"caller-{request}")
            .SetIdFormat(ActivityIdFormat.W3C)
            .SetParentId(
                ActivityTraceId.CreateRandom(),
                ActivitySpanId.CreateRandom(),
                sampled ? ActivityTraceFlags.Recorded : ActivityTraceFlags.None);
        return activity.Start();
    }

    private sealed class Submission(int wave, int index, int[] requests)
    {
        public int Wave { get; } = wave;
        public int Index { get; } = index;
        public int[] Requests { get; } = requests;
        public ActivityContext ParentContext { get; set; }
    }

    private sealed class MixedSubmission(int request, bool sampled)
    {
        public int Request { get; } = request;
        public bool Sampled { get; } = sampled;
        public ActivityContext ParentContext { get; set; }
    }

    private sealed record ExecuteObservation(ActivityContext Context, ActivitySpanId ParentSpanId);

    private sealed class MixedSamplingHandler : IRequestBatchHandler<int>
    {
        public TaskCompletionSource Started { get; } = NewSource();
        public TaskCompletionSource Release { get; } = NewSource();
        public ConcurrentBag<int[]> Batches { get; } = [];
        public ConcurrentBag<BatchObservation> BatchContexts { get; } = [];

        public async ValueTask HandleAsync(
            IReadOnlyList<int> requests,
            CancellationToken cancellationToken = default)
        {
            var copy = requests.ToArray();
            Batches.Add(copy);
            BatchContexts.Add(new BatchObservation(copy, Activity.Current?.Context ?? default));
            if (requests.Contains(0))
            {
                Started.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
        }
    }

    private sealed class HandlerObservations
    {
        public ConcurrentDictionary<int, int> ProcessedRequests { get; } = new();
        public ConcurrentBag<BatchObservation> Batches { get; } = [];

        public void Clear()
        {
            ProcessedRequests.Clear();
            Batches.Clear();
        }
    }

    private sealed class ObservingHandler(HandlerObservations observations) : IRequestBatchHandler<int>
    {
        public async ValueTask HandleAsync(IReadOnlyList<int> requests, CancellationToken cancellationToken = default)
        {
            observations.Batches.Add(new BatchObservation(requests.ToArray(), Activity.Current?.Context ?? default));
            foreach (var request in requests)
            {
                observations.ProcessedRequests.AddOrUpdate(request, 1, static (_, count) => count + 1);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(1), cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class ControlledHandler : IRequestBatchHandler<int>
    {
        public InvalidOperationException Failure { get; } = new("request 4 failed");
        public TaskCompletionSource Started { get; } = NewSource();
        public TaskCompletionSource Release { get; } = NewSource();
        public ConcurrentBag<int> ProcessedRequests { get; } = [];

        public async ValueTask HandleAsync(IReadOnlyList<int> requests, CancellationToken cancellationToken = default)
        {
            foreach (var request in requests)
            {
                ProcessedRequests.Add(request);
            }

            if (requests.Contains(1))
            {
                Started.TrySetResult();
                await Release.Task.ConfigureAwait(false);
            }

            if (requests.Contains(4))
            {
                throw Failure;
            }
        }
    }

    private sealed record BatchObservation(int[] Requests, ActivityContext Context);

    private sealed class SpanCapture : BaseProcessor<Activity>
    {
        public ConcurrentBag<SpanObservation> Spans { get; } = [];

        public override void OnEnd(Activity data)
        {
            if (data.OperationName is ExecuteOperation or BatchOperation)
            {
                Spans.Add(new SpanObservation(
                    data.OperationName,
                    data.Context,
                    data.ParentSpanId,
                    data.Links.ToArray(),
                    data.Status,
                    data.GetTagItem("requestbatcher.canceled") is true,
                    data.GetTagItem("requestbatcher.batch.submission.count") is int count ? count : null));
            }
        }

        public void Clear() => Spans.Clear();
    }

    private sealed record SpanObservation(
        string OperationName,
        ActivityContext Context,
        ActivitySpanId ParentSpanId,
        ActivityLink[] Links,
        ActivityStatusCode Status,
        bool Canceled,
        int? SubmissionCount);

    private static TaskCompletionSource NewSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
