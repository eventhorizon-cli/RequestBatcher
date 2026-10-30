using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using RequestBatcher.Diagnostics;

namespace RequestBatcher.Tests.Diagnostics;

[Collection("Tracing")]
public sealed class RequestBatcherMetricsTests
{
    [Fact]
    public void AddRequestBatcherInstrumentation_RegistersMeter()
    {
        var builder = Sdk.CreateMeterProviderBuilder();

        Assert.Same(builder, builder.AddRequestBatcherInstrumentation());
        using var provider = builder.Build();
    }

    [Fact]
    public async Task ExecuteAsync_WithMeterListener_RecordsSubmissionAndBatchMeasurements()
    {
        var measurements = new ConcurrentQueue<MeasurementRecord>();
        var instruments = new ConcurrentDictionary<string, Instrument>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == RequestBatcherMetrics.MeterName)
                {
                    instruments.TryAdd(instrument.Name, instrument);
                    meterListener.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            measurements.Enqueue(new MeasurementRecord(instrument.Name, value, tags)));
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
            measurements.Enqueue(new MeasurementRecord(instrument.Name, value, tags)));
        listener.Start();

        var services = new ServiceCollection();
        services.AddRequestBatcher<int, Handler>("prices", ServiceLifetime.Singleton);
        await using var provider = services.BuildServiceProvider();
        await provider.GetRequiredKeyedService<IRequestBatcher<int>>("prices").ExecuteAsync([1, 2, 3]);
        await provider.DisposeAsync();

        Assert.Equal(new[]
        {
            "requestbatcher.backlog",
            "requestbatcher.batch.size",
            "requestbatcher.execute.duration",
            "requestbatcher.handle.duration",
            "requestbatcher.submission.size",
        }, instruments.Keys.Order());
        Assert.IsType<ObservableGauge<long>>(instruments["requestbatcher.backlog"]);
        Assert.IsType<Histogram<long>>(instruments["requestbatcher.submission.size"]);
        Assert.IsType<Histogram<long>>(instruments["requestbatcher.batch.size"]);
        Assert.IsType<Histogram<double>>(instruments["requestbatcher.handle.duration"]);
        Assert.IsType<Histogram<double>>(instruments["requestbatcher.execute.duration"]);
        Assert.Equal(3, Assert.Single(measurements, item => item.Name == "requestbatcher.submission.size").Value);
        Assert.Equal(3, Assert.Single(measurements, item => item.Name == "requestbatcher.batch.size").Value);
        Assert.True(Assert.Single(measurements, item => item.Name == "requestbatcher.handle.duration").Value >= 0);
        Assert.True(Assert.Single(measurements, item => item.Name == "requestbatcher.execute.duration").Value >= 0);

        var tagged = Assert.Single(measurements, item => item.Name == "requestbatcher.submission.size");
        Assert.Equal(nameof(Int32), tagged.Tags["requestbatcher.request.type"]);
        Assert.Equal("prices", tagged.Tags["requestbatcher.name"]);
    }

    [Theory]
    [InlineData("requestbatcher.submission.size", 3)]
    [InlineData("requestbatcher.batch.size", 3)]
    [InlineData("requestbatcher.handle.duration", -1)]
    [InlineData("requestbatcher.execute.duration", -1)]
    public async Task MeterListener_SelectsOneInstrument_RecordsExpectedMeasurement(
        string selectedInstrument,
        double expectedValue)
    {
        var measurements = new ConcurrentQueue<MeasurementRecord>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == RequestBatcherMetrics.MeterName &&
                    instrument.Name == selectedInstrument)
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            measurements.Enqueue(new MeasurementRecord(instrument.Name, value, tags)));
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
            measurements.Enqueue(new MeasurementRecord(instrument.Name, value, tags)));
        listener.Start();

        var services = new ServiceCollection();
        services.AddRequestBatcher<int, Handler>("prices", ServiceLifetime.Singleton);
        await using var provider = services.BuildServiceProvider();
        await provider.GetRequiredKeyedService<IRequestBatcher<int>>("prices").ExecuteAsync([1, 2, 3]);
        await provider.DisposeAsync();

        var measurement = Assert.Single(measurements);
        Assert.Equal(selectedInstrument, measurement.Name);
        if (expectedValue < 0)
        {
            Assert.True(measurement.Value >= 0);
        }
        else
        {
            Assert.Equal(expectedValue, measurement.Value);
        }

        Assert.Equal("Int32", measurement.Tags["requestbatcher.request.type"]);
        Assert.Equal("prices", measurement.Tags["requestbatcher.name"]);
    }

    [Fact]
    public async Task ExecuteAsync_HandlerFailsAndQueuedRequestCanceled_SizeSumsRetainTheirBoundaries()
    {
        var measurements = new ConcurrentQueue<MeasurementRecord>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == RequestBatcherMetrics.MeterName &&
                    instrument.Name is "requestbatcher.submission.size" or "requestbatcher.batch.size")
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            measurements.Enqueue(new MeasurementRecord(instrument.Name, value, tags)));
        listener.Start();

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var expected = new InvalidOperationException("handler failed");
        var handler = new Mock<IRequestBatchHandler<int>>(MockBehavior.Strict);
        handler.Setup(value => value.HandleAsync(It.IsAny<IReadOnlyList<int>>(), It.IsAny<CancellationToken>()))
            .Returns(async (IReadOnlyList<int> requests, CancellationToken _) =>
            {
                Assert.Equal(1, Assert.Single(requests));
                entered.SetResult();
                await release.Task;
                throw expected;
            });
        var services = new ServiceCollection();
        services.AddSingleton(handler);
        services.AddRequestBatcher<int, MockHandlerProxy>(ServiceLifetime.Singleton, options =>
        {
            options.BatchSize = 1;
            options.MaxConcurrency = 1;
        });
        await using var provider = services.BuildServiceProvider();
        var batcher = provider.GetRequiredService<IRequestBatcher<int>>();
        var running = batcher.ExecuteAsync(1);
        using var cancellation = new CancellationTokenSource();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var queued = batcher.ExecuteAsync(new[] { 2 }, cancellation.Token);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued.WaitAsync(TimeSpan.FromSeconds(10)));
        }
        finally
        {
            release.TrySetResult();
        }
        Assert.Same(expected, await Assert.ThrowsAsync<InvalidOperationException>(() => running.WaitAsync(TimeSpan.FromSeconds(10))));
        await provider.DisposeAsync();

        var submissions = measurements.Where(item => item.Name == "requestbatcher.submission.size").ToArray();
        Assert.Equal(2, submissions.Length);
        Assert.Equal(2, submissions.Sum(item => item.Value));
        var consumed = Assert.Single(measurements, item => item.Name == "requestbatcher.batch.size");
        Assert.Equal(1, consumed.Value);
        handler.Verify(value => value.HandleAsync(It.IsAny<IReadOnlyList<int>>(), It.IsAny<CancellationToken>()), Times.Once);
        handler.VerifyNoOtherCalls();
    }

    [Fact]
    public void BatchScope_WithoutEnabledInstruments_DisposeRecordsNothing()
    {
        using var metrics = new RequestBatcherMetrics(3);
        var measurements = new ConcurrentQueue<MeasurementRecord>();
        using var listener = new MeterListener
        {
            InstrumentPublished = static (_, _) => { },
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            measurements.Enqueue(new MeasurementRecord(instrument.Name, value, tags)));
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
            measurements.Enqueue(new MeasurementRecord(instrument.Name, value, tags)));
        listener.Start();

        metrics.RecordSubmissionSize("Int32", "prices", "multi", 3);
        using (metrics.StartBatch("Int32", "prices", 3))
        {
        }

        Assert.Empty(measurements);
    }

    [Fact]
    public void MeterListener_EnableDisableInstrument_ControlsMeasurements()
    {
        using var metrics = new RequestBatcherMetrics(3);
        metrics.RecordSubmissionSize("Int32", "prices", "multi", 3);
        var measurements = new ConcurrentQueue<MeasurementRecord>();
        Instrument? batchSize = null;
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, _) =>
            {
                if (instrument.Meter.Name == RequestBatcherMetrics.MeterName &&
                    instrument.Name == "requestbatcher.batch.size")
                {
                    batchSize = instrument;
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            measurements.Enqueue(new MeasurementRecord(instrument.Name, value, tags)));
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
            measurements.Enqueue(new MeasurementRecord(instrument.Name, value, tags)));
        listener.Start();

        Assert.NotNull(batchSize);
        var instrument = batchSize!;
        Assert.False(instrument.Enabled);
        using (metrics.StartBatch("Int32", "prices", 3))
        {
        }
        Assert.Empty(measurements);

        listener.EnableMeasurementEvents(instrument);
        Assert.True(instrument.Enabled);
        using (metrics.StartBatch("Int32", "prices", 3))
        {
        }

        var measurement = Assert.Single(measurements);
        Assert.Equal("requestbatcher.batch.size", measurement.Name);
        Assert.Equal(3, measurement.Value);

        listener.DisableMeasurementEvents(instrument);
        Assert.False(instrument.Enabled);
        using (metrics.StartBatch("Int32", "prices", 3))
        {
        }

        Assert.Single(measurements);
    }

    [Fact]
    public void MeterListener_OneSubscriberDisposed_KeepsOtherSubscriberEnabled()
    {
        using var metrics = new RequestBatcherMetrics(3);
        metrics.RecordSubmissionSize("Int32", "prices", "multi", 3);
        var firstMeasurements = new ConcurrentQueue<MeasurementRecord>();
        var secondMeasurements = new ConcurrentQueue<MeasurementRecord>();
        Instrument? firstInstrument = null;
        Instrument? secondInstrument = null;
        var first = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == RequestBatcherMetrics.MeterName &&
                    instrument.Name == "requestbatcher.batch.size")
                {
                    firstInstrument = instrument;
                    meterListener.EnableMeasurementEvents(instrument);
                }
            },
        };
        first.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            firstMeasurements.Enqueue(new MeasurementRecord(instrument.Name, value, tags)));
        var second = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == RequestBatcherMetrics.MeterName &&
                    instrument.Name == "requestbatcher.batch.size")
                {
                    secondInstrument = instrument;
                    meterListener.EnableMeasurementEvents(instrument);
                }
            },
        };
        second.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            secondMeasurements.Enqueue(new MeasurementRecord(instrument.Name, value, tags)));

        try
        {
            first.Start();
            second.Start();
            Assert.NotNull(firstInstrument);
            Assert.NotNull(secondInstrument);
            var firstTarget = firstInstrument!;
            var secondTarget = secondInstrument!;
            Assert.True(firstTarget.Enabled);

            using (metrics.StartBatch("Int32", "prices", 3))
            {
            }

            Assert.Single(firstMeasurements);
            Assert.Single(secondMeasurements);

            first.Dispose();
            Assert.True(secondTarget.Enabled);
            using (metrics.StartBatch("Int32", "prices", 3))
            {
            }

            Assert.Single(firstMeasurements);
            Assert.Equal(2, secondMeasurements.Count);

            second.Dispose();
            Assert.False(secondTarget.Enabled);
        }
        finally
        {
            first.Dispose();
            second.Dispose();
        }
    }

    [Fact]
    public void StartBatch_NoSubscriberAtCreation_DoesNotBackfillMeasurement()
    {
        using var metrics = new RequestBatcherMetrics(3);
        var measurements = new ConcurrentQueue<MeasurementRecord>();
        Instrument? batchSize = null;
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, _) =>
            {
                if (instrument.Meter.Name == RequestBatcherMetrics.MeterName &&
                    instrument.Name == "requestbatcher.batch.size")
                {
                    batchSize = instrument;
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            measurements.Enqueue(new MeasurementRecord(instrument.Name, value, tags)));
        listener.Start();

        var scope = metrics.StartBatch("Int32", "prices", 3);
        Assert.NotNull(batchSize);
        var instrument = batchSize!;
        Assert.False(instrument.Enabled);
        listener.EnableMeasurementEvents(instrument);
        scope.Dispose();

        Assert.Empty(measurements);
        using (metrics.StartBatch("Int32", "prices", 3))
        {
        }

        Assert.Single(measurements);
    }

    [Theory]
    [InlineData(typeof(int), "Int32")]
    [InlineData(typeof(RequestBatchEntry<int, string>), "Int32")]
    public void GetName_Type_ReturnsSimpleRequestTypeName(
        Type requestType,
        string expected)
    {
        Assert.Equal(expected, RequestBatcherRequestType.GetName(requestType));
    }

    [Fact]
    public void AddRequestBatcherInstrumentation_NullMeterBuilder_ThrowsArgumentNullException()
    {
        var exception = Assert.Throws<ArgumentNullException>(() =>
            RequestBatcherMeterProviderBuilderExtensions.AddRequestBatcherInstrumentation(null!));
        Assert.Equal("builder", exception.ParamName);
    }

    private sealed class Handler : IRequestBatchHandler<int>
    {
        public ValueTask HandleAsync(IReadOnlyList<int> requests, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
    }

    private sealed class MockHandlerProxy(Mock<IRequestBatchHandler<int>> handler) : IRequestBatchHandler<int>
    {
        public ValueTask HandleAsync(IReadOnlyList<int> requests, CancellationToken cancellationToken = default) =>
            handler.Object.HandleAsync(requests, cancellationToken);
    }

    private readonly record struct MeasurementRecord(
        string Name,
        double Value,
        IReadOnlyDictionary<string, object?> Tags)
    {
        public MeasurementRecord(string name, long value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
            : this(name, value, ToDictionary(tags))
        {
        }

        public MeasurementRecord(string name, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
            : this(name, value, ToDictionary(tags))
        {
        }

        private static IReadOnlyDictionary<string, object?> ToDictionary(
            ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            var result = new Dictionary<string, object?>(tags.Length, StringComparer.Ordinal);
            foreach (var tag in tags)
            {
                result[tag.Key] = tag.Value;
            }

            return result;
        }
    }
}
