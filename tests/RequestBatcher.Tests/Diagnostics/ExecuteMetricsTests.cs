using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace RequestBatcher.Tests.Diagnostics;

[Collection("Tracing")]
public sealed class ExecuteMetricsTests
{
    private const string InstrumentName = "requestbatcher.execute.duration";
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
    public async Task ExecuteAsync_AllPublicShapes_RecordsOnlyEnabledMetricsAndTracingWithPublicTypeAndKind(
        bool response, bool batch, bool metrics, bool tracing)
    {
        using var capture = new Capture(metrics, tracing);
        if (response)
        {
            var handler = new Mock<IRequestBatchHandler<int, int>>(MockBehavior.Strict);
            handler.Setup(value => value.HandleAsync(It.IsAny<IReadOnlyList<RequestBatchEntry<int, int>>>(), It.IsAny<CancellationToken>()))
                .Returns((IReadOnlyList<RequestBatchEntry<int, int>> entries, CancellationToken _) =>
                {
                    entries.SetResponses(request => request * 10);
                    return ValueTask.CompletedTask;
                });
            await using var provider = CreateResponseProvider(handler, "prices");
            var batcher = provider.GetRequiredKeyedService<IRequestBatcher<int, int>>("prices");
            if (batch)
            {
                Assert.Equal(new[] { 10, 20 }, await batcher.ExecuteAsync(new[] { 1, 2 }).WaitAsync(Timeout));
            }
            else
            {
                Assert.Equal(10, await batcher.ExecuteAsync(1).WaitAsync(Timeout));
            }
            handler.VerifyAll();
        }
        else
        {
            var handler = Handler(static (_, _) => ValueTask.CompletedTask);
            await using var provider = CreateProvider(handler, name: "prices");
            var batcher = provider.GetRequiredKeyedService<IRequestBatcher<int>>("prices");
            await (batch ? batcher.ExecuteAsync(new[] { 1, 2 }) : batcher.ExecuteAsync(1)).WaitAsync(Timeout);
            handler.VerifyAll();
        }

        if (metrics)
        {
            var measurement = Assert.Single(capture.Measurements);
            Assert.Equal("ms", capture.Instrument!.Unit);
            AssertMeasurement(measurement, batch ? "multi" : "single", "success", "prices");
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

    [Fact]
    public async Task ExecuteAsync_ConcurrentMixedResponseSubmissions_RecordsEveryCallAndPreservesResponses()
    {
        const int producerCount = 4;
        const int callsPerProducer = 100;
        const int requestsPerProducer = callsPerProducer * 2;
        using var capture = new Capture();
        var handled = new int[producerCount * requestsPerProducer];
        var handler = new Mock<IRequestBatchHandler<int, int>>(MockBehavior.Strict);
        handler.Setup(value => value.HandleAsync(It.IsAny<IReadOnlyList<RequestBatchEntry<int, int>>>(), It.IsAny<CancellationToken>()))
            .Returns(async (IReadOnlyList<RequestBatchEntry<int, int>> entries, CancellationToken _) =>
            {
                await Task.Yield();
                for (var index = entries.Count - 1; index >= 0; index--)
                {
                    var entry = entries[index];
                    Interlocked.Increment(ref handled[entry.Request]);
                    entry.SetResponse(entry.Request * 17 + 3);
                }
            });
        await using var provider = CreateResponseProvider(handler, configure: options =>
        {
            options.BatchSize = 8;
            options.MaxConcurrency = 4;
        });
        var batcher = provider.GetRequiredService<IRequestBatcher<int, int>>();
        var start = NewSource();
        var producers = Enumerable.Range(0, producerCount).Select(producer => Task.Run(async () =>
        {
            await start.Task;
            var nextRequest = producer * requestsPerProducer;
            for (var call = 0; call < callsPerProducer; call++)
            {
                if (call % 2 == 0)
                {
                    var request = nextRequest++;
                    Assert.Equal(request * 17 + 3, await batcher.ExecuteAsync(request));
                }
                else
                {
                    var requests = new[] { nextRequest++, nextRequest++, nextRequest++ };
                    var responses = await batcher.ExecuteAsync(requests);
                    Assert.Equal(requests.Select(request => request * 17 + 3), responses);
                }
            }
        })).ToArray();
        start.SetResult();
        await Task.WhenAll(producers).WaitAsync(Timeout);

        Assert.All(handled, count => Assert.Equal(1, count));
        var measurements = capture.Measurements.ToArray();
        Assert.Equal(producerCount * callsPerProducer, measurements.Length);
        Assert.Equal(200, measurements.Count(item => Equals(item.Tags["requestbatcher.submission.kind"], "single")));
        Assert.Equal(200, measurements.Count(item => Equals(item.Tags["requestbatcher.submission.kind"], "multi")));
        Assert.All(measurements, measurement =>
            AssertMeasurement(measurement, Assert.IsType<string>(measurement.Tags["requestbatcher.submission.kind"]), "success"));
        handler.VerifyAll();
    }

    [Fact]
    public async Task ExecuteAsync_SplitGroup_RecordsOnlyAfterEveryHandlerCompletes()
    {
        using var capture = new Capture();
        var entered = new[] { NewSource(), NewSource(), NewSource() };
        var release = new[] { NewSource(), NewSource(), NewSource() };
        var finished = new[] { NewSource(), NewSource(), NewSource() };
        var handler = Handler(async (requests, _) =>
        {
            var index = Assert.Single(requests);
            entered[index].SetResult();
            await release[index].Task;
            finished[index].SetResult();
        });
        await using var provider = CreateProvider(handler, options =>
        {
            options.BatchSize = 1;
            options.MaxConcurrency = 3;
        });
        var submission = provider.GetRequiredService<IRequestBatcher<int>>().ExecuteAsync(new[] { 0, 1, 2 });
        var startedAt = Stopwatch.GetTimestamp();
        double elapsedBeforeLastRelease;
        try
        {
            await Task.WhenAll(entered.Select(source => source.Task)).WaitAsync(Timeout);
            release[0].SetResult();
            release[1].SetResult();
            await Task.WhenAll(finished.Take(2).Select(source => source.Task)).WaitAsync(Timeout);
            Assert.False(submission.IsCompleted);
            Assert.Empty(capture.Measurements);
            elapsedBeforeLastRelease = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
        }
        finally
        {
            foreach (var source in release)
            {
                source.TrySetResult();
            }
        }
        await submission.WaitAsync(Timeout);
        var measurement = Assert.Single(capture.Measurements);
        AssertMeasurement(measurement, "multi", "success");
        Assert.True(measurement.Value >= elapsedBeforeLastRelease);
    }

    [Fact]
    public async Task ExecuteAsync_MergedSubmissions_RecordsEachCallRatherThanEachHandler()
    {
        using var capture = new Capture();
        var entered = NewSource();
        var release = NewSource();
        var batches = new ConcurrentQueue<int[]>();
        var handler = Handler(async (requests, _) =>
        {
            batches.Enqueue(requests.ToArray());
            if (requests.Contains(0))
            {
                entered.SetResult();
                await release.Task;
            }
        });
        await using var provider = CreateProvider(handler, options => options.BatchSize = 10);
        var batcher = provider.GetRequiredService<IRequestBatcher<int>>();
        var leader = batcher.ExecuteAsync(0);
        var single = Task.CompletedTask;
        var group = Task.CompletedTask;
        try
        {
            await entered.Task.WaitAsync(Timeout);
            single = batcher.ExecuteAsync(1);
            group = batcher.ExecuteAsync(new[] { 2, 3 });
            Assert.Empty(capture.Measurements);
        }
        finally
        {
            release.TrySetResult();
        }
        await Task.WhenAll(leader, single, group).WaitAsync(Timeout);
        Assert.Equal(2, batches.Count);
        Assert.Equal(new[] { 1, 2, 3 }, batches.Last());
        Assert.Equal(3, capture.Measurements.Count);
        Assert.Equal(2, capture.Measurements.Count(item => Equals(item.Tags["requestbatcher.submission.kind"], "single")));
        Assert.Single(capture.Measurements, item => Equals(item.Tags["requestbatcher.submission.kind"], "multi"));
    }

    [Fact]
    public async Task ExecuteAsync_CapacityWaitCanceled_RecordsCancellationWithoutCompletingRunningCalls()
    {
        using var capture = new Capture();
        var entered = NewSource();
        var release = NewSource();
        var handler = Handler(async (requests, _) =>
        {
            if (requests.Contains(0))
            {
                entered.SetResult();
                await release.Task;
            }
        });
        await using var provider = CreateProvider(handler, options =>
        {
            options.BatchSize = 1;
            options.MaxPendingRequests = 1;
        });
        var batcher = provider.GetRequiredService<IRequestBatcher<int>>();
        using var cancellation = new CancellationTokenSource();
        var leader = batcher.ExecuteAsync(0);
        var queued = Task.CompletedTask;
        try
        {
            await entered.Task.WaitAsync(Timeout);
            queued = batcher.ExecuteAsync(1);
            var waiting = batcher.ExecuteAsync(new[] { 2 }, cancellation.Token);
            Assert.False(waiting.IsCompleted);
            Assert.Empty(capture.Measurements);
            cancellation.Cancel();
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.WaitAsync(Timeout));
            Assert.Equal(cancellation.Token, error.CancellationToken);
            Assert.True(waiting.IsCanceled);
            AssertMeasurement(Assert.Single(capture.Measurements), "multi", "canceled");
            Assert.False(leader.IsCompleted);
            Assert.False(queued.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
        }
        await Task.WhenAll(leader, queued).WaitAsync(Timeout);
        Assert.Equal(3, capture.Measurements.Count);
    }

    [Fact]
    public async Task ExecuteAsync_CanceledAfterDispatch_RecordsActualSuccessfulOutcome()
    {
        using var capture = new Capture();
        var entered = NewSource();
        var release = NewSource();
        var handler = Handler(async (_, _) =>
        {
            entered.SetResult();
            await release.Task;
        });
        await using var provider = CreateProvider(handler);
        using var cancellation = new CancellationTokenSource();
        var submission = provider.GetRequiredService<IRequestBatcher<int>>().ExecuteAsync(1, cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(Timeout);
            cancellation.Cancel();
            Assert.False(submission.IsCompleted);
            Assert.Empty(capture.Measurements);
        }
        finally
        {
            release.TrySetResult();
        }
        await submission.WaitAsync(Timeout);
        AssertMeasurement(Assert.Single(capture.Measurements), "single", "success");
    }

    [Fact]
    public async Task ExecuteAsync_GroupHasDifferentFailures_PreservesAllFaultsAndRecordsOnce()
    {
        using var capture = new Capture();
        var first = new InvalidOperationException("first");
        var second = new ArgumentException("second");
        var entered = new[] { NewSource(), NewSource(), NewSource() };
        var release = NewSource();
        var handler = Handler(async (requests, _) =>
        {
            var index = Assert.Single(requests);
            entered[index].SetResult();
            await release.Task;
            if (index == 0)
            {
                throw first;
            }
            if (index == 1)
            {
                throw second;
            }
        });
        await using var provider = CreateProvider(handler, options =>
        {
            options.BatchSize = 1;
            options.MaxConcurrency = 3;
        });
        var submission = provider.GetRequiredService<IRequestBatcher<int>>().ExecuteAsync(new[] { 0, 1, 2 });
        try
        {
            await Task.WhenAll(entered.Select(source => source.Task)).WaitAsync(Timeout);
            Assert.Empty(capture.Measurements);
        }
        finally
        {
            release.TrySetResult();
        }
        await Assert.ThrowsAnyAsync<Exception>(() => submission.WaitAsync(Timeout));
        Assert.True(submission.IsFaulted);
        Assert.Equal(2, submission.Exception!.InnerExceptions.Count);
        Assert.Contains(first, submission.Exception.InnerExceptions);
        Assert.Contains(second, submission.Exception.InnerExceptions);
        AssertMeasurement(Assert.Single(capture.Measurements), "multi", "error");
    }

    [Fact]
    public async Task ExecuteAsync_HandlerThrowsCancellationException_PreservesFaultedStatus()
    {
        using var capture = new Capture();
        var expected = new OperationCanceledException("Handler failure, not caller cancellation");
        var handler = Handler((_, _) => ValueTask.FromException(expected));
        await using var provider = CreateProvider(handler);
        var submission = provider.GetRequiredService<IRequestBatcher<int>>().ExecuteAsync(1);
        Assert.Same(expected, await Assert.ThrowsAsync<OperationCanceledException>(() => submission));
        Assert.True(submission.IsFaulted);
        AssertMeasurement(Assert.Single(capture.Measurements), "single", "error");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_PreCanceledAndEmptyCalls_RecordEachTerminalOutcome(bool response)
    {
        using var capture = new Capture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        if (response)
        {
            var handler = new Mock<IRequestBatchHandler<int, int>>(MockBehavior.Strict);
            await using var provider = CreateResponseProvider(handler);
            var batcher = provider.GetRequiredService<IRequestBatcher<int, int>>();
            Assert.Empty(await batcher.ExecuteAsync(Array.Empty<int>()));
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => batcher.ExecuteAsync(1, cancellation.Token));
            Assert.Equal(cancellation.Token, error.CancellationToken);
            Assert.Throws<ArgumentNullException>(() => { _ = batcher.ExecuteAsync((IEnumerable<int>)null!); });
            handler.VerifyNoOtherCalls();
        }
        else
        {
            var handler = new Mock<IRequestBatchHandler<int>>(MockBehavior.Strict);
            await using var provider = CreateProvider(handler);
            var batcher = provider.GetRequiredService<IRequestBatcher<int>>();
            await batcher.ExecuteAsync(Array.Empty<int>());
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => batcher.ExecuteAsync(1, cancellation.Token));
            Assert.Equal(cancellation.Token, error.CancellationToken);
            Assert.Throws<ArgumentNullException>(() => { _ = batcher.ExecuteAsync((IEnumerable<int>)null!); });
            handler.VerifyNoOtherCalls();
        }
        Assert.Equal(2, capture.Measurements.Count);
        AssertMeasurement(capture.Measurements.First(), "multi", "success");
        AssertMeasurement(capture.Measurements.Last(), "single", "canceled");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_EnumerationFails_RecordsErrorWithoutHandlerDispatch(bool response)
    {
        using var capture = new Capture();
        var expected = new InvalidOperationException("enumeration failed");
        IEnumerable<int> Requests()
        {
            yield return 1;
            throw expected;
        }
        if (response)
        {
            var handler = new Mock<IRequestBatchHandler<int, int>>(MockBehavior.Strict);
            await using var provider = CreateResponseProvider(handler);
            Assert.Same(expected, await Assert.ThrowsAsync<InvalidOperationException>(
                () => provider.GetRequiredService<IRequestBatcher<int, int>>().ExecuteAsync(Requests())));
            handler.VerifyNoOtherCalls();
        }
        else
        {
            var handler = new Mock<IRequestBatchHandler<int>>(MockBehavior.Strict);
            await using var provider = CreateProvider(handler);
            Assert.Same(expected, await Assert.ThrowsAsync<InvalidOperationException>(
                () => provider.GetRequiredService<IRequestBatcher<int>>().ExecuteAsync(Requests())));
            handler.VerifyNoOtherCalls();
        }
        AssertMeasurement(Assert.Single(capture.Measurements), "multi", "error");
    }

    [Fact]
    public async Task ExecuteAsync_QueueFullAndStopped_RecordRejectedCalls()
    {
        using var capture = new Capture();
        var entered = NewSource();
        var release = NewSource();
        var handler = Handler(async (requests, _) =>
        {
            if (requests.Contains(0))
            {
                entered.SetResult();
                await release.Task;
            }
        });
        await using var provider = CreateProvider(handler, options =>
        {
            options.BatchSize = 1;
            options.MaxPendingRequests = 1;
            options.FullMode = RequestBatchFullMode.Fail;
        });
        var batcher = provider.GetRequiredService<IRequestBatcher<int>>();
        var leader = batcher.ExecuteAsync(0);
        var queued = Task.CompletedTask;
        try
        {
            await entered.Task.WaitAsync(Timeout);
            queued = batcher.ExecuteAsync(1);
            await Assert.ThrowsAsync<RequestBatchQueueFullException>(() => batcher.ExecuteAsync(new[] { 2 }));
            AssertMeasurement(Assert.Single(capture.Measurements), "multi", "error");
        }
        finally
        {
            release.TrySetResult();
        }
        await Task.WhenAll(leader, queued).WaitAsync(Timeout);
        await ((IAsyncDisposable)batcher).DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => batcher.ExecuteAsync(3));
        Assert.Equal(4, capture.Measurements.Count);
        AssertMeasurement(capture.Measurements.Last(), "single", "error");
        await provider.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => batcher.ExecuteAsync(4));
        Assert.Equal(4, capture.Measurements.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_SubscriptionChangesDuringCall_DoesNotReplayOrRecordDisabledCalls(bool initiallyEnabled)
    {
        using var capture = new Capture(initiallyEnabled);
        var entered = NewSource();
        var release = NewSource();
        var handler = Handler(async (_, _) =>
        {
            entered.TrySetResult();
            await release.Task;
        });
        await using var provider = CreateProvider(handler);
        var batcher = provider.GetRequiredService<IRequestBatcher<int>>();
        var submission = batcher.ExecuteAsync(1);
        try
        {
            await entered.Task.WaitAsync(Timeout);
            capture.SetEnabled(!initiallyEnabled);
        }
        finally
        {
            release.TrySetResult();
        }
        await submission.WaitAsync(Timeout);
        Assert.Empty(capture.Measurements);
        capture.SetEnabled(true);
        await batcher.ExecuteAsync(2).WaitAsync(Timeout);
        AssertMeasurement(Assert.Single(capture.Measurements), "single", "success");
    }

    [Fact]
    public async Task ExecuteAsync_TracingAlsoEnabled_RecordsOneMetricAndOneCallSpan()
    {
        using var capture = new Capture();
        var spans = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "RequestBatcher",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = spans.Enqueue,
        };
        ActivitySource.AddActivityListener(listener);
        var handler = Handler(static (_, _) => ValueTask.CompletedTask);
        await using var provider = CreateProvider(handler);
        await provider.GetRequiredService<IRequestBatcher<int>>().ExecuteAsync(new[] { 1, 2 }).WaitAsync(Timeout);
        AssertMeasurement(Assert.Single(capture.Measurements), "multi", "success");
        Assert.Single(spans, span => span.OperationName == "RequestBatcher.Execute");
    }

    private static Mock<IRequestBatchHandler<int>> Handler(Func<IReadOnlyList<int>, CancellationToken, ValueTask> callback)
    {
        var handler = new Mock<IRequestBatchHandler<int>>(MockBehavior.Strict);
        handler.Setup(value => value.HandleAsync(It.IsAny<IReadOnlyList<int>>(), It.IsAny<CancellationToken>()))
            .Returns(callback);
        return handler;
    }

    private static ServiceProvider CreateProvider(
        Mock<IRequestBatchHandler<int>> handler,
        Action<RequestBatchOptions<int>>? configure = null,
        string? name = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(handler);
        if (name is null)
        {
            services.AddRequestBatcher<int, HandlerProxy>(ServiceLifetime.Singleton, configure);
        }
        else
        {
            services.AddRequestBatcher<int, HandlerProxy>(name, ServiceLifetime.Singleton, configure);
        }
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    private static ServiceProvider CreateResponseProvider(
        Mock<IRequestBatchHandler<int, int>> handler,
        string? name = null,
        Action<RequestBatchOptions<int>>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(handler);
        if (name is null)
        {
            services.AddRequestBatcher<int, int, ResponseHandlerProxy>(ServiceLifetime.Singleton, configure);
        }
        else
        {
            services.AddRequestBatcher<int, int, ResponseHandlerProxy>(name, ServiceLifetime.Singleton, configure);
        }
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
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
        private readonly MeterListener _listener;
        private readonly ActivityListener? _activityListener;

        public Capture(bool metricsEnabled = true, bool tracingEnabled = false)
        {
            _listener = new MeterListener
            {
                InstrumentPublished = (instrument, listener) =>
                {
                    if (instrument.Meter.Name == "RequestBatcher" && instrument.Name == InstrumentName)
                    {
                        Instrument = instrument;
                        if (metricsEnabled)
                        {
                            listener.EnableMeasurementEvents(instrument);
                        }
                    }
                },
            };
            _listener.SetMeasurementEventCallback<double>((_, value, tags, _) =>
                Measurements.Enqueue(new Measurement(value, tags.ToArray().ToDictionary(tag => tag.Key, tag => tag.Value))));
            _listener.Start();
            if (tracingEnabled)
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

        public Instrument? Instrument { get; private set; }
        public ConcurrentQueue<Measurement> Measurements { get; } = new();
        public ConcurrentQueue<Activity> Spans { get; } = new();

        public void SetEnabled(bool enabled)
        {
            Assert.NotNull(Instrument);
            if (enabled)
            {
                _listener.EnableMeasurementEvents(Instrument);
            }
            else
            {
                _listener.DisableMeasurementEvents(Instrument);
            }
        }

        public void Dispose()
        {
            _activityListener?.Dispose();
            _listener.Dispose();
        }
    }

    private sealed record Measurement(double Value, Dictionary<string, object?> Tags);

    private sealed class HandlerProxy(Mock<IRequestBatchHandler<int>> handler) : IRequestBatchHandler<int>
    {
        public ValueTask HandleAsync(IReadOnlyList<int> requests, CancellationToken cancellationToken = default) =>
            handler.Object.HandleAsync(requests, cancellationToken);
    }

    private sealed class ResponseHandlerProxy(Mock<IRequestBatchHandler<int, int>> handler) : IRequestBatchHandler<int, int>
    {
        public ValueTask HandleAsync(IReadOnlyList<RequestBatchEntry<int, int>> entries, CancellationToken cancellationToken = default) =>
            handler.Object.HandleAsync(entries, cancellationToken);
    }
}
