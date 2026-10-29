using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace RequestBatcher.Tests.Diagnostics;

[Collection("Tracing")]
public sealed class SubmissionSizeMetricsTests
{
    private const string SubmissionInstrument = "requestbatcher.submission.size";
    private const string BatchInstrument = "requestbatcher.batch.size";
    private static TimeSpan Timeout => TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ExecuteAsync_AllPublicShapes_RecordsOneSizeWithPublicTags(bool response, bool multi)
    {
        using var capture = new Capture();
        var enumerations = 0;
        IEnumerable<int> Requests()
        {
            enumerations++;
            yield return 7;
        }

        if (response)
        {
            var handler = ResponseHandler();
            await using var provider = CreateResponseProvider(handler, "prices");
            var batcher = provider.GetRequiredKeyedService<IRequestBatcher<int, int>>("prices");
            if (multi)
            {
                Assert.Equal(new[] { 70 }, await batcher.ExecuteAsync(Requests()).WaitAsync(Timeout));
            }
            else
            {
                Assert.Equal(70, await batcher.ExecuteAsync(7).WaitAsync(Timeout));
            }
            handler.VerifyAll();
        }
        else
        {
            var handler = Handler(static (_, _) => ValueTask.CompletedTask);
            await using var provider = CreateProvider(handler, name: "prices");
            var batcher = provider.GetRequiredKeyedService<IRequestBatcher<int>>("prices");
            await (multi ? batcher.ExecuteAsync(Requests()) : batcher.ExecuteAsync(7)).WaitAsync(Timeout);
            handler.VerifyAll();
        }

        Assert.Equal(multi ? 1 : 0, enumerations);
        AssertSubmission(Assert.Single(capture.Measurements), 1, multi ? "multi" : "single", "prices");
        Assert.IsType<Histogram<long>>(capture.Submission);
        Assert.Equal("{request}", capture.Submission!.Unit);
        Assert.NotNull(capture.ExecuteDuration);
        Assert.False(capture.ExecuteDuration.Enabled);
        Assert.Empty(capture.Durations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_EmptyCollection_RecordsZeroWithoutDispatch(bool response)
    {
        using var capture = new Capture();
        if (response)
        {
            var handler = new Mock<IRequestBatchHandler<int, int>>(MockBehavior.Strict);
            await using var provider = CreateResponseProvider(handler);
            Assert.Empty(await provider.GetRequiredService<IRequestBatcher<int, int>>()
                .ExecuteAsync(Array.Empty<int>()).WaitAsync(Timeout));
            handler.VerifyNoOtherCalls();
        }
        else
        {
            var handler = new Mock<IRequestBatchHandler<int>>(MockBehavior.Strict);
            await using var provider = CreateProvider(handler);
            await provider.GetRequiredService<IRequestBatcher<int>>().ExecuteAsync(Array.Empty<int>()).WaitAsync(Timeout);
            handler.VerifyNoOtherCalls();
        }
        AssertSubmission(Assert.Single(capture.Measurements), 0, "multi");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_RejectedBeforeAdmission_RecordsNothingAndDoesNotEnumerateEarly(bool response)
    {
        using var capture = new Capture();
        var enumerations = 0;
        var expected = new InvalidOperationException("enumeration failed");
        IEnumerable<int> FailingRequests()
        {
            enumerations++;
            yield return 1;
            throw expected;
        }

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        if (response)
        {
            var handler = new Mock<IRequestBatchHandler<int, int>>(MockBehavior.Strict);
            await using var provider = CreateResponseProvider(handler);
            var batcher = provider.GetRequiredService<IRequestBatcher<int, int>>();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => batcher.ExecuteAsync(1, cancellation.Token));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => batcher.ExecuteAsync(FailingRequests(), cancellation.Token));
            Assert.Equal(0, enumerations);
            Assert.Throws<ArgumentNullException>(() => { _ = batcher.ExecuteAsync((IEnumerable<int>)null!); });
            Assert.Same(expected, await Assert.ThrowsAsync<InvalidOperationException>(() => batcher.ExecuteAsync(FailingRequests())));
            await provider.DisposeAsync();
            await Assert.ThrowsAsync<ObjectDisposedException>(() => batcher.ExecuteAsync(1));
            await Assert.ThrowsAsync<ObjectDisposedException>(() => batcher.ExecuteAsync(FailingRequests()));
            handler.VerifyNoOtherCalls();
        }
        else
        {
            var handler = new Mock<IRequestBatchHandler<int>>(MockBehavior.Strict);
            await using var provider = CreateProvider(handler);
            var batcher = provider.GetRequiredService<IRequestBatcher<int>>();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => batcher.ExecuteAsync(1, cancellation.Token));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => batcher.ExecuteAsync(FailingRequests(), cancellation.Token));
            Assert.Equal(0, enumerations);
            Assert.Throws<ArgumentNullException>(() => { _ = batcher.ExecuteAsync((IEnumerable<int>)null!); });
            Assert.Same(expected, await Assert.ThrowsAsync<InvalidOperationException>(() => batcher.ExecuteAsync(FailingRequests())));
            await provider.DisposeAsync();
            await Assert.ThrowsAsync<ObjectDisposedException>(() => batcher.ExecuteAsync(1));
            await Assert.ThrowsAsync<ObjectDisposedException>(() => batcher.ExecuteAsync(FailingRequests()));
            handler.VerifyNoOtherCalls();
        }
        Assert.Equal(1, enumerations);
        Assert.Empty(capture.Measurements);
    }

    [Fact]
    public async Task ExecuteAsync_GroupExceedsCapacityAndBatchSize_RecordsOneSubmissionAndMultipleBatches()
    {
        using var capture = new Capture(includeBatches: true);
        var entered = NewSource();
        var release = NewSource();
        var handled = new ConcurrentQueue<int>();
        var handler = Handler(async (requests, _) =>
        {
            if (requests.Contains(0))
            {
                entered.SetResult();
                await release.Task;
            }
            foreach (var request in requests)
            {
                handled.Enqueue(request);
            }
        });
        await using var provider = CreateProvider(handler, options =>
        {
            options.BatchSize = 2;
            options.MaxPendingRequests = 2;
            options.MaxConcurrency = 1;
        });
        var submission = provider.GetRequiredService<IRequestBatcher<int>>().ExecuteAsync(Enumerable.Range(0, 7));
        try
        {
            await entered.Task.WaitAsync(Timeout);
            Assert.False(submission.IsCompleted);
            AssertSubmission(Assert.Single(capture.Measurements, item => item.Instrument == SubmissionInstrument), 7, "multi");
        }
        finally
        {
            release.TrySetResult();
        }
        await submission.WaitAsync(Timeout);
        await provider.DisposeAsync();
        AssertSubmission(Assert.Single(capture.Measurements, item => item.Instrument == SubmissionInstrument), 7, "multi");
        var batches = capture.Measurements.Where(item => item.Instrument == BatchInstrument).ToArray();
        Assert.True(batches.Length >= 4);
        Assert.All(batches, batch => Assert.InRange(batch.Value, 1, 2));
        Assert.Equal(7, batches.Sum(batch => batch.Value));
        Assert.Equal(Enumerable.Range(0, 7), handled.Order());
        handler.VerifyAll();
    }

    [Fact]
    public async Task ExecuteAsync_TwoSubmissionsMerged_RecordsSeparateSubmissionSizesAndCombinedBatchSize()
    {
        using var capture = new Capture(includeBatches: true);
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
            options.BatchSize = 8;
            options.MaxConcurrency = 1;
        });
        var batcher = provider.GetRequiredService<IRequestBatcher<int>>();
        var blocker = batcher.ExecuteAsync(0);
        var single = Task.CompletedTask;
        var multi = Task.CompletedTask;
        try
        {
            await entered.Task.WaitAsync(Timeout);
            single = batcher.ExecuteAsync(1);
            multi = batcher.ExecuteAsync(new[] { 2, 3 });
            var submissions = capture.Measurements.Where(item => item.Instrument == SubmissionInstrument).ToArray();
            Assert.Equal(3, submissions.Length);
            AssertSubmission(submissions[1], 1, "single");
            AssertSubmission(submissions[2], 2, "multi");
        }
        finally
        {
            release.TrySetResult();
        }
        await Task.WhenAll(blocker, single, multi).WaitAsync(Timeout);
        await provider.DisposeAsync();
        Assert.Equal(3, capture.Measurements.Count(item => item.Instrument == SubmissionInstrument));
        Assert.Equal(new long[] { 1, 3 }, capture.Measurements.Where(item => item.Instrument == BatchInstrument).Select(item => item.Value));
        handler.VerifyAll();
    }

    [Fact]
    public async Task ExecuteAsync_QueueFullAfterAdmission_RetainsSubmissionSize()
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
            options.MaxConcurrency = 1;
            options.MaxPendingRequests = 1;
            options.FullMode = RequestBatchFullMode.Fail;
        });
        var batcher = provider.GetRequiredService<IRequestBatcher<int>>();
        var blocker = batcher.ExecuteAsync(0);
        var queued = Task.CompletedTask;
        try
        {
            await entered.Task.WaitAsync(Timeout);
            queued = batcher.ExecuteAsync(1);
            await Assert.ThrowsAsync<RequestBatchQueueFullException>(() => batcher.ExecuteAsync(new[] { 2 }));
            Assert.Equal(3, capture.Measurements.Count);
            AssertSubmission(capture.Measurements.Last(), 1, "multi");
        }
        finally
        {
            release.TrySetResult();
        }
        await Task.WhenAll(blocker, queued).WaitAsync(Timeout);
        Assert.Equal(3, capture.Measurements.Count);
        handler.VerifyAll();
    }

    private static Mock<IRequestBatchHandler<int>> Handler(Func<IReadOnlyList<int>, CancellationToken, ValueTask> callback)
    {
        var handler = new Mock<IRequestBatchHandler<int>>(MockBehavior.Strict);
        handler.Setup(value => value.HandleAsync(It.IsAny<IReadOnlyList<int>>(), It.IsAny<CancellationToken>()))
            .Returns(callback);
        return handler;
    }

    private static Mock<IRequestBatchHandler<int, int>> ResponseHandler()
    {
        var handler = new Mock<IRequestBatchHandler<int, int>>(MockBehavior.Strict);
        handler.Setup(value => value.HandleAsync(It.IsAny<IReadOnlyList<RequestBatchEntry<int, int>>>(), It.IsAny<CancellationToken>()))
            .Returns((IReadOnlyList<RequestBatchEntry<int, int>> entries, CancellationToken _) =>
            {
                entries.SetResponses(request => request * 10);
                return ValueTask.CompletedTask;
            });
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

    private static ServiceProvider CreateResponseProvider(Mock<IRequestBatchHandler<int, int>> handler, string? name = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(handler);
        if (name is null)
        {
            services.AddRequestBatcher<int, int, ResponseHandlerProxy>(ServiceLifetime.Singleton);
        }
        else
        {
            services.AddRequestBatcher<int, int, ResponseHandlerProxy>(name, ServiceLifetime.Singleton);
        }
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    private static TaskCompletionSource NewSource() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static void AssertSubmission(Measurement measurement, long size, string kind, string? name = null)
    {
        Assert.Equal(SubmissionInstrument, measurement.Instrument);
        Assert.Equal(size, measurement.Value);
        Assert.Equal("Int32", measurement.Tags["requestbatcher.request.type"]);
        Assert.Equal(kind, measurement.Tags["requestbatcher.submission.kind"]);
        Assert.Equal(name is null ? 2 : 3, measurement.Tags.Count);
        if (name is not null)
        {
            Assert.Equal(name, measurement.Tags["requestbatcher.name"]);
        }
    }

    private sealed class Capture : IDisposable
    {
        private readonly MeterListener _listener;

        public Capture(bool includeBatches = false)
        {
            _listener = new MeterListener
            {
                InstrumentPublished = (instrument, listener) =>
                {
                    if (instrument.Meter.Name != "RequestBatcher")
                    {
                        return;
                    }
                    if (instrument.Name == SubmissionInstrument)
                    {
                        Submission = instrument;
                        listener.EnableMeasurementEvents(instrument);
                    }
                    else if (includeBatches && instrument.Name == BatchInstrument)
                    {
                        listener.EnableMeasurementEvents(instrument);
                    }
                    else if (instrument.Name == "requestbatcher.execute.duration")
                    {
                        ExecuteDuration = instrument;
                    }
                },
            };
            _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
                Measurements.Enqueue(new Measurement(instrument.Name, value, tags.ToArray().ToDictionary(tag => tag.Key, tag => tag.Value))));
            _listener.SetMeasurementEventCallback<double>((_, value, _, _) => Durations.Enqueue(value));
            _listener.Start();
        }

        public Instrument? Submission { get; private set; }
        public Instrument? ExecuteDuration { get; private set; }
        public ConcurrentQueue<Measurement> Measurements { get; } = new();
        public ConcurrentQueue<double> Durations { get; } = new();
        public void Dispose() => _listener.Dispose();
    }

    private sealed record Measurement(string Instrument, long Value, Dictionary<string, object?> Tags);

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
