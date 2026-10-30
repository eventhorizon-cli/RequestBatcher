using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace RequestBatcher.Tests.Diagnostics;

[Collection("Tracing")]
public sealed class DelegateExecuteMetricsTests
{
    private static TimeSpan Timeout => TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(false, false, false, true)]
    [InlineData(false, false, true, false)]
    [InlineData(false, false, true, true)]
    [InlineData(false, true, false, false)]
    [InlineData(false, true, false, true)]
    [InlineData(false, true, true, false)]
    [InlineData(false, true, true, true)]
    [InlineData(true, false, false, false)]
    [InlineData(true, false, false, true)]
    [InlineData(true, false, true, false)]
    [InlineData(true, false, true, true)]
    [InlineData(true, true, false, false)]
    [InlineData(true, true, false, true)]
    [InlineData(true, true, true, false)]
    [InlineData(true, true, true, true)]
    public async Task ExecuteAsync_AllDelegateShapes_RecordsOnlyEnabledMetricsAndTracing(
        bool response, bool multi, bool metrics, bool tracing)
    {
        using var capture = new Capture(metrics, tracing);
        var entered = NewSource();
        var release = NewSource();
        var services = new ServiceCollection();
        if (response)
        {
            var handler = new Mock<IRequestBatchHandler<int, int>>(MockBehavior.Strict);
            handler.Setup(value => value.HandleAsync(It.IsAny<IReadOnlyList<RequestBatchEntry<int, int>>>(), It.IsAny<CancellationToken>()))
                .Returns(async (IReadOnlyList<RequestBatchEntry<int, int>> entries, CancellationToken _) =>
                {
                    entered.TrySetResult();
                    await release.Task;
                    for (var index = entries.Count - 1; index >= 0; index--)
                    {
                        entries[index].SetResponse(entries[index].Request * 10);
                    }
                });
            services.AddSingleton(handler.Object);
            services.AddDelegateRequestBatcher<int, int>("prices", options => options.BatchSize = 2);
            await using var provider = services.BuildServiceProvider();
            var batcher = provider.GetRequiredKeyedService<IRequestBatcher<int, int>>("prices");
            if (multi)
            {
                var submission = batcher.ExecuteAsync(new[] { 3, 1, 3 },
                    static (IReadOnlyList<RequestBatchEntry<int, int>> entries, IRequestBatchHandler<int, int> service, CancellationToken token) =>
                        service.HandleAsync(entries, token));
                await AssertPendingAndReleaseAsync(submission, entered, release, capture);
                Assert.Equal(new[] { 30, 10, 30 }, await submission.WaitAsync(Timeout));
            }
            else
            {
                var submission = batcher.ExecuteAsync(3,
                    static (IReadOnlyList<RequestBatchEntry<int, int>> entries, IRequestBatchHandler<int, int> service, CancellationToken token) =>
                        service.HandleAsync(entries, token));
                await AssertPendingAndReleaseAsync(submission, entered, release, capture);
                Assert.Equal(30, await submission.WaitAsync(Timeout));
            }
            handler.VerifyAll();
        }
        else
        {
            var handled = new ConcurrentQueue<int>();
            var handler = new Mock<IRequestBatchHandler<int>>(MockBehavior.Strict);
            handler.Setup(value => value.HandleAsync(It.IsAny<IReadOnlyList<int>>(), It.IsAny<CancellationToken>()))
                .Returns(async (IReadOnlyList<int> requests, CancellationToken _) =>
                {
                    entered.TrySetResult();
                    await release.Task;
                    foreach (var request in requests)
                    {
                        handled.Enqueue(request);
                    }
                });
            services.AddSingleton(handler.Object);
            services.AddDelegateRequestBatcher<int>("prices", options => options.BatchSize = 2);
            await using var provider = services.BuildServiceProvider();
            var batcher = provider.GetRequiredKeyedService<IRequestBatcher<int>>("prices");
            Delegate callback = static (IReadOnlyList<int> requests, IRequestBatchHandler<int> service, CancellationToken token) =>
                service.HandleAsync(requests, token);
            var submission = multi
                ? batcher.ExecuteAsync(new[] { 3, 1, 3 }, callback)
                : batcher.ExecuteAsync(3, callback);
            await AssertPendingAndReleaseAsync(submission, entered, release, capture);
            await submission.WaitAsync(Timeout);
            Assert.Equal(multi ? new[] { 1, 3, 3 } : [3], handled.Order());
            handler.VerifyAll();
        }

        if (metrics)
        {
            AssertMeasurement(Assert.Single(capture.Measurements), multi ? "multi" : "single", "success", "prices");
        }
        else
        {
            Assert.Empty(capture.Measurements);
        }

        var callSpans = capture.Spans.Where(span => span.OperationName == "RequestBatcher.Execute prices").ToArray();
        Assert.Equal(tracing ? 1 : 0, callSpans.Length);
        if (tracing)
        {
            Assert.Equal("Int32", callSpans[0].GetTagItem("requestbatcher.request.type"));
            Assert.Equal("prices", callSpans[0].GetTagItem("requestbatcher.name"));
            Assert.Equal(ActivityStatusCode.Unset, callSpans[0].Status);
        }
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    public async Task ExecuteAsync_EmptyOrOneItemCollection_RecordsMultiSuccess(bool response, int count)
    {
        using var capture = new Capture(tracing: false);
        var requests = Enumerable.Range(1, count).ToArray();
        var services = new ServiceCollection();
        if (response)
        {
            services.AddDelegateRequestBatcher<int, int>();
            await using var provider = services.BuildServiceProvider();
            var actual = await provider.GetRequiredService<IRequestBatcher<int, int>>().ExecuteAsync(requests,
                static (IReadOnlyList<RequestBatchEntry<int, int>> entries) => entries.SetResponses(static request => request * 10))
                .WaitAsync(Timeout);
            Assert.Equal(requests.Select(request => request * 10), actual);
        }
        else
        {
            services.AddDelegateRequestBatcher<int>();
            await using var provider = services.BuildServiceProvider();
            await provider.GetRequiredService<IRequestBatcher<int>>().ExecuteAsync(requests,
                static (IReadOnlyList<int> items) => Assert.Equal(1, Assert.Single(items)))
                .WaitAsync(Timeout);
        }
        AssertMeasurement(Assert.Single(capture.Measurements), "multi", "success");
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ExecuteAsync_BindingFailureAndPreCancellation_RecordActualOutcomes(bool response, bool tracing)
    {
        using var capture = new Capture(metrics: true, tracing: tracing);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Delegate invalid = static (int first, int second) => Assert.Fail("An invalid delegate must never execute.");
        var services = new ServiceCollection();
        if (response)
        {
            services.AddDelegateRequestBatcher<int, int>("prices");
            await using var provider = services.BuildServiceProvider();
            var batcher = provider.GetRequiredKeyedService<IRequestBatcher<int, int>>("prices");
            Assert.Throws<ArgumentException>(() => { _ = batcher.ExecuteAsync(1, invalid); });
            Assert.Throws<ArgumentException>(() => { _ = batcher.ExecuteAsync(new[] { 1 }, invalid); });
            // The invalid signature would throw if a pre-canceled call attempted to bind it.
            var single = batcher.ExecuteAsync(1, invalid, cancellation.Token);
            var multi = batcher.ExecuteAsync(new[] { 1 }, invalid, cancellation.Token);
            Assert.Equal(cancellation.Token, (await Assert.ThrowsAnyAsync<OperationCanceledException>(() => single)).CancellationToken);
            Assert.Equal(cancellation.Token, (await Assert.ThrowsAnyAsync<OperationCanceledException>(() => multi)).CancellationToken);
            Assert.True(single.IsCanceled);
            Assert.True(multi.IsCanceled);
        }
        else
        {
            services.AddDelegateRequestBatcher<int>("prices");
            await using var provider = services.BuildServiceProvider();
            var batcher = provider.GetRequiredKeyedService<IRequestBatcher<int>>("prices");
            Assert.Throws<ArgumentException>(() => { _ = batcher.ExecuteAsync(1, invalid); });
            Assert.Throws<ArgumentException>(() => { _ = batcher.ExecuteAsync(new[] { 1 }, invalid); });
            var single = batcher.ExecuteAsync(1, invalid, cancellation.Token);
            var multi = batcher.ExecuteAsync(new[] { 1 }, invalid, cancellation.Token);
            Assert.Equal(cancellation.Token, (await Assert.ThrowsAnyAsync<OperationCanceledException>(() => single)).CancellationToken);
            Assert.Equal(cancellation.Token, (await Assert.ThrowsAnyAsync<OperationCanceledException>(() => multi)).CancellationToken);
            Assert.True(single.IsCanceled);
            Assert.True(multi.IsCanceled);
        }

        var measurements = capture.Measurements.ToArray();
        Assert.Equal(4, measurements.Length);
        AssertMeasurement(measurements[0], "single", "error", "prices");
        AssertMeasurement(measurements[1], "multi", "error", "prices");
        AssertMeasurement(measurements[2], "single", "canceled", "prices");
        AssertMeasurement(measurements[3], "multi", "canceled", "prices");
        var callSpans = capture.Spans.Where(span => span.OperationName == "RequestBatcher.Execute prices").ToArray();
        Assert.Equal(tracing ? 4 : 0, callSpans.Length);
        if (tracing)
        {
            Assert.Equal(ActivityStatusCode.Error, callSpans[0].Status);
            Assert.Equal(nameof(ArgumentException), callSpans[0].GetTagItem("error.type"));
            Assert.Equal(ActivityStatusCode.Error, callSpans[1].Status);
            Assert.Equal(nameof(ArgumentException), callSpans[1].GetTagItem("error.type"));
            Assert.Equal(ActivityStatusCode.Unset, callSpans[2].Status);
            Assert.Equal(true, callSpans[2].GetTagItem("requestbatcher.canceled"));
            Assert.Equal(ActivityStatusCode.Unset, callSpans[3].Status);
            Assert.Equal(true, callSpans[3].GetTagItem("requestbatcher.canceled"));
            Assert.All(callSpans, span =>
            {
                Assert.Equal("Int32", span.GetTagItem("requestbatcher.request.type"));
                Assert.Equal("prices", span.GetTagItem("requestbatcher.name"));
            });
        }
    }

    private static async Task AssertPendingAndReleaseAsync(
        Task submission, TaskCompletionSource entered, TaskCompletionSource release, Capture capture)
    {
        try
        {
            await entered.Task.WaitAsync(Timeout);
            Assert.False(submission.IsCompleted);
            Assert.Empty(capture.Measurements);
        }
        finally
        {
            release.TrySetResult();
        }
    }

    private static TaskCompletionSource NewSource() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static void AssertMeasurement(Measurement measurement, string kind, string outcome, string? name = null)
    {
        Assert.True(measurement.Value >= 0);
        Assert.Equal("Int32", measurement.Tags["requestbatcher.request.type"]);
        Assert.Equal(kind, measurement.Tags["requestbatcher.submission.kind"]);
        Assert.Equal(outcome, measurement.Tags["requestbatcher.outcome"]);
        Assert.Equal(name is null ? 3 : 4, measurement.Tags.Count);
        if (name is not null)
        {
            Assert.Equal(name, measurement.Tags["requestbatcher.name"]);
        }
    }

    private sealed class Capture : IDisposable
    {
        private readonly MeterListener _meterListener;
        private readonly ActivityListener? _activityListener;

        public Capture(bool metrics = true, bool tracing = false)
        {
            _meterListener = new MeterListener
            {
                InstrumentPublished = (instrument, listener) =>
                {
                    if (instrument.Meter.Name == "RequestBatcher" && instrument.Name == "requestbatcher.execute.duration")
                    {
                        if (metrics)
                        {
                            listener.EnableMeasurementEvents(instrument);
                        }
                    }
                },
            };
            _meterListener.SetMeasurementEventCallback<double>((_, value, tags, _) =>
                Measurements.Enqueue(new Measurement(value, tags.ToArray().ToDictionary(tag => tag.Key, tag => tag.Value))));
            _meterListener.Start();
            if (tracing)
            {
                _activityListener = new ActivityListener
                {
                    ShouldListenTo = source => source.Name == "RequestBatcher",
                    Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                    ActivityStopped = Spans.Enqueue,
                };
                ActivitySource.AddActivityListener(_activityListener);
            }
        }

        public ConcurrentQueue<Measurement> Measurements { get; } = new();
        public ConcurrentQueue<Activity> Spans { get; } = new();

        public void Dispose()
        {
            _activityListener?.Dispose();
            _meterListener.Dispose();
        }
    }

    private sealed record Measurement(double Value, Dictionary<string, object?> Tags);
}
