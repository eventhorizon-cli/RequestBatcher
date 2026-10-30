using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using BufferQueue;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using RequestBatcher.Diagnostics;

namespace RequestBatcher.Tests.Diagnostics;

[Collection("Tracing")]
public sealed class BacklogMetricsTests
{
    private static TimeSpan Timeout => TimeSpan.FromSeconds(10);

    [Fact]
    public void RegisterBacklog_OnlyReadsOnCollection_EmitsAvailableZero()
    {
        var consumer = new LagConsumer<int>(() => (true, new(0, BufferOffsetUnit.Items)));
        using var capture = new Capture();
        using var metrics = new RequestBatcherMetrics(1);
        using var registration = metrics.RegisterBacklog(consumer, "Int32", "prices");

        Assert.Equal(0, consumer.ReadCount);
        metrics.RecordSubmissionSize("Int32", "prices", "single", 1);
        Assert.Equal(0, consumer.ReadCount);

        var measurement = Assert.Single(capture.Collect());
        Assert.Equal(1, consumer.ReadCount);
        AssertBacklog(measurement, 0, "Int32", "prices");
        Assert.Equal(0, consumer.ConsumeCount);
        Assert.Equal(0, consumer.CommitCount);
    }

    [Fact]
    public void RegisterBacklog_EmitsOnlyAvailableItemSnapshots_AndSaturatesOverflow()
    {
        using var capture = new Capture();
        using var metrics = new RequestBatcherMetrics(1);
        using var zero = metrics.RegisterBacklog(
            new LagConsumer<int>(() => (true, new(0, BufferOffsetUnit.Items))), "Int32", "zero");
        using var unavailable = metrics.RegisterBacklog(
            new LagConsumer<int>(() => (false, new(4, BufferOffsetUnit.Items))), "Int32", "unavailable");
        using var bytes = metrics.RegisterBacklog(
            new LagConsumer<int>(() => (true, new(4, BufferOffsetUnit.Bytes))), "Int32", "bytes");
        using var unknown = metrics.RegisterBacklog(
            new LagConsumer<int>(() => (true, new(4, (BufferOffsetUnit)int.MaxValue))), "Int32", "unknown");
        using var overflow = metrics.RegisterBacklog(
            new LagConsumer<int>(() => (true, new(ulong.MaxValue, BufferOffsetUnit.Items))), "Int32", "overflow");

        var measurements = capture.Collect();

        Assert.Equal(2, measurements.Length);
        AssertBacklog(Assert.Single(measurements, item => item.Tags["requestbatcher.name"]?.Equals("zero") == true), 0, "Int32", "zero");
        AssertBacklog(Assert.Single(measurements, item => item.Tags["requestbatcher.name"]?.Equals("overflow") == true), long.MaxValue, "Int32", "overflow");
    }

    [Fact]
    public void RegisterBacklog_SeparatesNamedSources_AndPublishesGaugeMetadata()
    {
        using var capture = new Capture();
        using var metrics = new RequestBatcherMetrics(1);
        using var first = metrics.RegisterBacklog(
            new LagConsumer<int>(() => (true, new(3, BufferOffsetUnit.Items))), "Int32", "orders");
        using var second = metrics.RegisterBacklog(
            new LagConsumer<int>(() => (true, new(7, BufferOffsetUnit.Items))), "Int32", "prices");

        Assert.IsType<ObservableGauge<long>>(capture.BacklogInstrument);
        Assert.Equal("{request}", capture.BacklogInstrument!.Unit);
        var measurements = capture.Collect();
        Assert.Equal(2, measurements.Length);
        AssertBacklog(Assert.Single(measurements, item => Equals(item.Tags["requestbatcher.name"], "orders")), 3, "Int32", "orders");
        AssertBacklog(Assert.Single(measurements, item => Equals(item.Tags["requestbatcher.name"], "prices")), 7, "Int32", "prices");
    }

    [Fact]
    public void RegisterBacklog_ListenerEnabledAfterRegistration_ObservesSubsequentCollections()
    {
        var consumer = new LagConsumer<int>(() => (true, new(5, BufferOffsetUnit.Items)));
        using var capture = new Capture(enableBacklog: false);
        using var metrics = new RequestBatcherMetrics(1);
        using var registration = metrics.RegisterBacklog(consumer, "Int32", null);

        Assert.Empty(capture.Collect());
        Assert.Equal(0, consumer.ReadCount);

        capture.EnableBacklog();
        AssertBacklog(Assert.Single(capture.Collect()), 5, "Int32", null);
        Assert.Equal(1, consumer.ReadCount);
    }

    [Fact]
    public void RegisterBacklog_RegistrationBeforeListener_IsObservedWhenListenerStarts()
    {
        var consumer = new LagConsumer<int>(() => (true, new(4, BufferOffsetUnit.Items)));
        using var metrics = new RequestBatcherMetrics(1);
        using var registration = metrics.RegisterBacklog(consumer, "Int32", null);
        using var capture = new Capture();

        AssertBacklog(Assert.Single(capture.Collect()), 4, "Int32", null);
        Assert.Equal(1, consumer.ReadCount);
    }

    [Fact]
    public async Task RegisterBacklog_DisposeDuringCollection_AllowsCurrentReadAndRemovesFutureReads()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var consumer = new LagConsumer<int>(() =>
        {
            entered.TrySetResult();
            release.Task.GetAwaiter().GetResult();
            return (true, new BufferConsumerLagSnapshot(2, BufferOffsetUnit.Items));
        });
        using var capture = new Capture();
        using var metrics = new RequestBatcherMetrics(1);
        var registration = metrics.RegisterBacklog(consumer, "Int32", null);
        Task<Measurement[]>? collection = null;
        try
        {
            collection = Task.Run(capture.Collect);
            await entered.Task.WaitAsync(Timeout);
            registration.Dispose();
            registration.Dispose();
            release.TrySetResult();

            AssertBacklog(Assert.Single(await collection.WaitAsync(Timeout)), 2, "Int32", null);
            Assert.Empty(capture.Collect());
            Assert.Equal(1, consumer.ReadCount);
        }
        finally
        {
            registration.Dispose();
            release.TrySetResult();
            if (collection is not null)
            {
                await collection.WaitAsync(Timeout);
            }
        }
    }

    [Fact]
    public void Dispose_WithBacklogSources_RemovesSourcesAndRejectsNewRegistrations()
    {
        var consumer = new LagConsumer<int>(() => (true, new(2, BufferOffsetUnit.Items)));
        using var capture = new Capture();
        var metrics = new RequestBatcherMetrics(1);
        using var registration = metrics.RegisterBacklog(consumer, "Int32", null);

        AssertBacklog(Assert.Single(capture.Collect()), 2, "Int32", null);
        metrics.Dispose();
        metrics.Dispose();

        Assert.Empty(capture.Collect());
        Assert.Equal(1, consumer.ReadCount);
        Assert.Throws<ObjectDisposedException>(() => metrics.RegisterBacklog(consumer, "Int32", null));
    }

    [Fact]
    public async Task Coordinator_ReportsQueuedMemoryItemsAcrossPartitions_IncludingCanceledUntilPulled()
    {
        using var capture = new Capture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new Mock<IRequestBatchHandler<int>>(MockBehavior.Strict);
        handler.Setup(value => value.HandleAsync(It.IsAny<IReadOnlyList<int>>(), It.IsAny<CancellationToken>()))
            .Returns(async (IReadOnlyList<int> requests, CancellationToken _) =>
            {
                if (requests.Contains(1))
                {
                    entered.TrySetResult();
                    await release.Task;
                }
            });
        var services = new ServiceCollection();
        services.AddSingleton(handler);
        services.AddRequestBatcher<int, HandlerProxy>("prices", ServiceLifetime.Singleton, options =>
        {
            options.BatchSize = 1;
            options.MaxConcurrency = 1;
            options.MaxPendingRequests = 8;
            options.UsePartitionKey(static request => request, partitionCount: 2);
        });
        await using var provider = services.BuildServiceProvider();
        var batcher = provider.GetRequiredKeyedService<IRequestBatcher<int>>("prices");
        var active = batcher.ExecuteAsync(1);
        using var cancellation = new CancellationTokenSource();
        var canceled = Task.CompletedTask;
        var queued = Task.CompletedTask;
        try
        {
            await entered.Task.WaitAsync(Timeout);
            canceled = batcher.ExecuteAsync(2, cancellation.Token);
            queued = batcher.ExecuteAsync(3);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled.WaitAsync(Timeout));

            var backlog = Assert.Single(capture.Collect());
            AssertBacklog(backlog, 2, "Int32", "prices");
        }
        finally
        {
            release.TrySetResult();
        }

        await Task.WhenAll(active, queued).WaitAsync(Timeout);
        Assert.Equal(0, Assert.Single(capture.Collect()).Value);
        await provider.DisposeAsync();
        Assert.Empty(capture.Collect());
        handler.Verify(value => value.HandleAsync(It.IsAny<IReadOnlyList<int>>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        handler.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Coordinator_CapacityWaitingRequests_AreExcludedFromBacklog()
    {
        using var capture = new Capture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new Mock<IRequestBatchHandler<int>>(MockBehavior.Strict);
        handler.Setup(value => value.HandleAsync(It.IsAny<IReadOnlyList<int>>(), It.IsAny<CancellationToken>()))
            .Returns(async (IReadOnlyList<int> requests, CancellationToken _) =>
            {
                if (requests.Contains(1))
                {
                    entered.TrySetResult();
                    await release.Task;
                }
            });
        var services = new ServiceCollection();
        services.AddSingleton(handler);
        services.AddRequestBatcher<int, HandlerProxy>("capacity", ServiceLifetime.Singleton, options =>
        {
            options.BatchSize = 1;
            options.MaxConcurrency = 1;
            options.MaxPendingRequests = 1;
        });
        await using var provider = services.BuildServiceProvider();
        var batcher = provider.GetRequiredKeyedService<IRequestBatcher<int>>("capacity");
        var active = batcher.ExecuteAsync(1);
        var queued = Task.CompletedTask;
        var waiting = Task.CompletedTask;
        try
        {
            await entered.Task.WaitAsync(Timeout);
            queued = batcher.ExecuteAsync(2);
            AssertBacklog(Assert.Single(capture.Collect()), 1, "Int32", "capacity");

            waiting = batcher.ExecuteAsync(3);
            Assert.False(waiting.IsCompleted);
            AssertBacklog(Assert.Single(capture.Collect()), 1, "Int32", "capacity");
        }
        finally
        {
            release.TrySetResult();
        }

        await Task.WhenAll(active, queued, waiting).WaitAsync(Timeout);
        await provider.DisposeAsync();
        Assert.Empty(capture.Collect());
        handler.Verify(value => value.HandleAsync(It.IsAny<IReadOnlyList<int>>(), It.IsAny<CancellationToken>()), Times.Exactly(3));
        handler.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task DisposeAsync_Draining_KeepsBacklogObservableUntilDisposed()
    {
        using var capture = new Capture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new Mock<IRequestBatchHandler<int>>(MockBehavior.Strict);
        handler.Setup(value => value.HandleAsync(It.IsAny<IReadOnlyList<int>>(), It.IsAny<CancellationToken>()))
            .Returns(async (IReadOnlyList<int> requests, CancellationToken _) =>
            {
                if (requests.Contains(1))
                {
                    entered.TrySetResult();
                    await release.Task;
                }
            });
        var services = new ServiceCollection();
        services.AddSingleton(handler);
        services.AddRequestBatcher<int, HandlerProxy>("drain", ServiceLifetime.Singleton, options =>
        {
            options.BatchSize = 1;
            options.MaxConcurrency = 1;
            options.MaxPendingRequests = 4;
        });
        var provider = services.BuildServiceProvider();
        var batcher = provider.GetRequiredKeyedService<IRequestBatcher<int>>("drain");
        var active = batcher.ExecuteAsync(1);
        var queued = Task.CompletedTask;
        Task? disposal = null;
        try
        {
            await entered.Task.WaitAsync(Timeout);
            queued = batcher.ExecuteAsync(2);
            disposal = provider.DisposeAsync().AsTask();

            Assert.False(disposal.IsCompleted);
            AssertBacklog(Assert.Single(capture.Collect()), 1, "Int32", "drain");
        }
        finally
        {
            release.TrySetResult();
            disposal ??= provider.DisposeAsync().AsTask();
            await disposal.WaitAsync(Timeout);
        }

        await Task.WhenAll(active, queued).WaitAsync(Timeout);
        Assert.Empty(capture.Collect());
        handler.Verify(value => value.HandleAsync(It.IsAny<IReadOnlyList<int>>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        handler.VerifyNoOtherCalls();
    }

    [Fact]
    public void AddRequestBatcherInstrumentation_Backlog_ExportsLongGaugeWithPublicTags()
    {
        var consumer = new LagConsumer<int>(() => (true, new(6, BufferOffsetUnit.Items)));
        using var metrics = new RequestBatcherMetrics(1);
        using var registration = metrics.RegisterBacklog(consumer, "Int32", "quotes");
        var exporter = new GaugeExporter();
        var reader = new BaseExportingMetricReader(exporter);
        using var provider = Sdk.CreateMeterProviderBuilder()
            .AddRequestBatcherInstrumentation()
            .AddReader(reader)
            .Build();

        Assert.True(reader.Collect((int)Timeout.TotalMilliseconds));

        var metric = Assert.Single(exporter.Points);
        Assert.Equal("RequestBatcher", metric.MeterName);
        Assert.Equal("requestbatcher.backlog", metric.Name);
        Assert.Equal(MetricType.LongGauge, metric.Type);
        Assert.Equal(6, metric.Value);
        Assert.Equal("Int32", metric.Tags["requestbatcher.request.type"]);
        Assert.Equal("quotes", metric.Tags["requestbatcher.name"]);
        Assert.Equal(2, metric.Tags.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Coordinator_OrdinaryAndDelegateResponseShapes_RegisterOnePublicTypeSource(bool delegated)
    {
        using var capture = new Capture();
        var services = new ServiceCollection();
        if (delegated)
        {
            services.AddDelegateRequestBatcher<int, int>("quotes");
        }
        else
        {
            services.AddSingleton<ResponseHandler>();
            services.AddRequestBatcher<int, int, ResponseHandler>("quotes", ServiceLifetime.Singleton);
        }

        await using var provider = services.BuildServiceProvider();
        var batcher = provider.GetRequiredKeyedService<IRequestBatcher<int, int>>("quotes");
        var result = delegated
            ? await batcher.ExecuteAsync(7,
                static (IReadOnlyList<RequestBatchEntry<int, int>> entries) => entries.SetResponses(static request => request * 10))
            : await batcher.ExecuteAsync(7);
        Assert.Equal(70, result);

        var measurement = Assert.Single(capture.Collect());
        AssertBacklog(measurement, 0, "Int32", "quotes");
        await provider.DisposeAsync();
        Assert.Empty(capture.Collect());
    }

    private static void AssertBacklog(Measurement measurement, long value, string requestType, string? name)
    {
        Assert.Equal("requestbatcher.backlog", measurement.Name);
        Assert.Equal(value, measurement.Value);
        Assert.Equal(requestType, measurement.Tags["requestbatcher.request.type"]);
        Assert.Equal(name is null ? 1 : 2, measurement.Tags.Count);
        if (name is not null)
        {
            Assert.Equal(name, measurement.Tags["requestbatcher.name"]);
        }
    }

    private sealed class Capture : IDisposable
    {
        private readonly MeterListener _listener;

        public Capture(bool enableBacklog = true)
        {
            _listener = new MeterListener
            {
                InstrumentPublished = (instrument, listener) =>
                {
                    if (instrument.Meter.Name == RequestBatcherMetrics.MeterName &&
                        instrument.Name == "requestbatcher.backlog")
                    {
                        BacklogInstrument = instrument;
                        if (enableBacklog)
                        {
                            listener.EnableMeasurementEvents(instrument);
                        }
                    }
                },
            };
            _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
                Measurements.Enqueue(new Measurement(instrument.Name, value, ToDictionary(tags))));
            _listener.Start();
        }

        public Instrument? BacklogInstrument { get; private set; }
        private ConcurrentQueue<Measurement> Measurements { get; } = new();

        public void EnableBacklog() => _listener.EnableMeasurementEvents(BacklogInstrument!);

        public Measurement[] Collect()
        {
            _listener.RecordObservableInstruments();
            var measurements = new List<Measurement>();
            while (Measurements.TryDequeue(out var measurement))
            {
                measurements.Add(measurement);
            }

            return measurements.ToArray();
        }

        public void Dispose() => _listener.Dispose();

        private static Dictionary<string, object?> ToDictionary(ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            var result = new Dictionary<string, object?>(tags.Length, StringComparer.Ordinal);
            foreach (var tag in tags)
            {
                result[tag.Key] = tag.Value;
            }

            return result;
        }
    }

    private sealed record Measurement(string Name, long Value, Dictionary<string, object?> Tags);

    private sealed class GaugeExporter : BaseExporter<Metric>
    {
        public List<ExportedGauge> Points { get; } = [];

        public override ExportResult Export(in Batch<Metric> batch)
        {
            Points.Clear();
            foreach (var metric in batch)
            {
                foreach (ref readonly var point in metric.GetMetricPoints())
                {
                    var tags = new Dictionary<string, object?>(StringComparer.Ordinal);
                    foreach (var tag in point.Tags)
                    {
                        tags[tag.Key] = tag.Value;
                    }

                    Points.Add(new ExportedGauge(
                        metric.MeterName,
                        metric.Name,
                        metric.MetricType,
                        point.GetGaugeLastValueLong(),
                        tags));
                }
            }

            return ExportResult.Success;
        }
    }

    private sealed record ExportedGauge(
        string MeterName,
        string Name,
        MetricType Type,
        long Value,
        Dictionary<string, object?> Tags);

    private sealed class LagConsumer<T>(Func<(bool Available, BufferConsumerLagSnapshot Snapshot)> read)
        : IBufferPullConsumer<T>
    {
        private int _readCount;
        private int _consumeCount;
        private int _commitCount;

        public string TopicName => "test";
        public string GroupName => "test";
        public int ReadCount => Volatile.Read(ref _readCount);
        public int ConsumeCount => Volatile.Read(ref _consumeCount);
        public int CommitCount => Volatile.Read(ref _commitCount);

        public bool TryGetLagSnapshot(out BufferConsumerLagSnapshot snapshot)
        {
            Interlocked.Increment(ref _readCount);
            var result = read();
            snapshot = result.Snapshot;
            return result.Available;
        }

        public IAsyncEnumerable<IReadOnlyList<T>> ConsumeAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _consumeCount);
            throw new InvalidOperationException("Backlog collection must not consume queue items.");
        }

        public ValueTask CommitAsync()
        {
            Interlocked.Increment(ref _commitCount);
            throw new InvalidOperationException("Backlog collection must not commit queue items.");
        }
    }

    private sealed class HandlerProxy(Mock<IRequestBatchHandler<int>> handler) : IRequestBatchHandler<int>
    {
        public ValueTask HandleAsync(IReadOnlyList<int> requests, CancellationToken cancellationToken = default) =>
            handler.Object.HandleAsync(requests, cancellationToken);
    }

    private sealed class ResponseHandler : IRequestBatchHandler<int, int>
    {
        public ValueTask HandleAsync(
            IReadOnlyList<RequestBatchEntry<int, int>> entries,
            CancellationToken cancellationToken = default)
        {
            entries.SetResponses(static request => request * 10);
            return ValueTask.CompletedTask;
        }
    }
}
