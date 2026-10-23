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
