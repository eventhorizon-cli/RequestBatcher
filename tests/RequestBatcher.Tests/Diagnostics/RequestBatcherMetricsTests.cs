using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
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
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == RequestBatcherMetrics.MeterName)
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

        Assert.Equal(3, Assert.Single(measurements, item => item.Name == "requestbatcher.requests.submitted").Value);
        Assert.Equal(3, Assert.Single(measurements, item => item.Name == "requestbatcher.requests.processed").Value);
        Assert.Equal(1, Assert.Single(measurements, item => item.Name == "requestbatcher.batches.processed").Value);
        Assert.Equal(3, Assert.Single(measurements, item => item.Name == "requestbatcher.batch.size").Value);
        Assert.Contains(measurements, item => item.Name == "requestbatcher.batch.duration");

        var tagged = Assert.Single(measurements, item => item.Name == "requestbatcher.requests.submitted");
        Assert.Equal(nameof(Int32), tagged.Tags["requestbatcher.request.type"]);
        Assert.Equal("prices", tagged.Tags["requestbatcher.name"]);
    }

    [Theory]
    [InlineData("requestbatcher.requests.submitted", 3)]
    [InlineData("requestbatcher.requests.processed", 3)]
    [InlineData("requestbatcher.batches.processed", 1)]
    [InlineData("requestbatcher.batch.size", 3)]
    [InlineData("requestbatcher.batch.duration", -1)]
    public void MeterListener_SelectsOneInstrument_RecordsExpectedMeasurement(
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

        RequestBatcherMetrics.RecordSubmitted("Int32", "prices", 3);
        using (RequestBatcherMetrics.StartBatch("Int32", "prices", 3))
        {
        }

        var measurement = Assert.Single(measurements);
        Assert.Equal(selectedInstrument, measurement.Name);
        if (selectedInstrument == "requestbatcher.batch.duration")
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
    public void BatchScope_WithoutEnabledInstruments_DisposeRecordsNothing()
    {
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

        RequestBatcherMetrics.RecordSubmitted("Int32", "prices", 3);
        using (RequestBatcherMetrics.StartBatch("Int32", "prices", 3))
        {
        }

        Assert.Empty(measurements);
    }

    [Fact]
    public void MeterListener_EnableDisableInstrument_ControlsMeasurements()
    {
        RequestBatcherMetrics.RecordSubmitted("Int32", "prices", 3);
        var measurements = new ConcurrentQueue<MeasurementRecord>();
        Instrument? processedRequests = null;
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, _) =>
            {
                if (instrument.Meter.Name == RequestBatcherMetrics.MeterName &&
                    instrument.Name == "requestbatcher.requests.processed")
                {
                    processedRequests = instrument;
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            measurements.Enqueue(new MeasurementRecord(instrument.Name, value, tags)));
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
            measurements.Enqueue(new MeasurementRecord(instrument.Name, value, tags)));
        listener.Start();

        Assert.NotNull(processedRequests);
        var instrument = processedRequests!;
        Assert.False(instrument.Enabled);
        using (RequestBatcherMetrics.StartBatch("Int32", "prices", 3))
        {
        }
        Assert.Empty(measurements);

        listener.EnableMeasurementEvents(instrument);
        Assert.True(instrument.Enabled);
        using (RequestBatcherMetrics.StartBatch("Int32", "prices", 3))
        {
        }

        var measurement = Assert.Single(measurements);
        Assert.Equal("requestbatcher.requests.processed", measurement.Name);
        Assert.Equal(3, measurement.Value);

        listener.DisableMeasurementEvents(instrument);
        Assert.False(instrument.Enabled);
        using (RequestBatcherMetrics.StartBatch("Int32", "prices", 3))
        {
        }

        Assert.Single(measurements);
    }

    [Fact]
    public void MeterListener_OneSubscriberDisposed_KeepsOtherSubscriberEnabled()
    {
        RequestBatcherMetrics.RecordSubmitted("Int32", "prices", 3);
        var firstMeasurements = new ConcurrentQueue<MeasurementRecord>();
        var secondMeasurements = new ConcurrentQueue<MeasurementRecord>();
        Instrument? firstInstrument = null;
        Instrument? secondInstrument = null;
        var first = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == RequestBatcherMetrics.MeterName &&
                    instrument.Name == "requestbatcher.requests.processed")
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
                    instrument.Name == "requestbatcher.requests.processed")
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

            using (RequestBatcherMetrics.StartBatch("Int32", "prices", 3))
            {
            }

            Assert.Single(firstMeasurements);
            Assert.Single(secondMeasurements);

            first.Dispose();
            Assert.True(secondTarget.Enabled);
            using (RequestBatcherMetrics.StartBatch("Int32", "prices", 3))
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
        var measurements = new ConcurrentQueue<MeasurementRecord>();
        Instrument? processedRequests = null;
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, _) =>
            {
                if (instrument.Meter.Name == RequestBatcherMetrics.MeterName &&
                    instrument.Name == "requestbatcher.requests.processed")
                {
                    processedRequests = instrument;
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            measurements.Enqueue(new MeasurementRecord(instrument.Name, value, tags)));
        listener.Start();

        var scope = RequestBatcherMetrics.StartBatch("Int32", "prices", 3);
        Assert.NotNull(processedRequests);
        var instrument = processedRequests!;
        Assert.False(instrument.Enabled);
        listener.EnableMeasurementEvents(instrument);
        scope.Dispose();

        Assert.Empty(measurements);
        using (RequestBatcherMetrics.StartBatch("Int32", "prices", 3))
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
