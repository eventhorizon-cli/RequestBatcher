using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using Xunit;

namespace RequestBatcher.Observability.IntegrationTests;

[Collection(MetricsAdviceCollection.Name)]
public sealed class MetricsAdviceTests
{
    private const string MeterName = "RequestBatcher";
    private const string BatchSizeName = "requestbatcher.batch.size";
    private const string SubmissionSizeName = "requestbatcher.submission.size";
    private const string BatcherNameTag = "requestbatcher.name";
    private const string RequestTypeTag = "requestbatcher.request.type";
    private const string SubmissionKindTag = "requestbatcher.submission.kind";

    private static readonly long[] _batchBounds =
    [
        0, 1, 6, 11, 17, 22, 28, 33, 39, 44, 50, 55, 60, 66, 71, 77, 82, 88, 93, 99, 104, 110, 115, 120
    ];

    private static readonly long[] _submissionBounds =
    [
        0, 1, 8, 15, 22, 29, 36, 43, 50, 57, 64, 71, 78, 85, 92, 99, 106, 113, 120, 240, 480, 960, 1920,
        3840
    ];

    [Fact]
    public async Task ExecuteAsync_DirectMeterProvider_UsesContainerMaximumAdviceForBothSizeHistograms()
    {
        var services = new ServiceCollection();
        var invocations = new InvocationCapture();
        services.AddSingleton(invocations);
        services.AddSingleton<SmallBatchHandler>();
        services.AddRequestBatcher<int, int, SmallBatchHandler>("small", ServiceLifetime.Singleton,
            options => options.BatchSize = 40);
        services.AddDelegateRequestBatcher<int, int>("large", options => options.BatchSize = 120);

        await using var serviceProvider = services.BuildServiceProvider();
        var published = new ConcurrentDictionary<string, HistogramObservation>();
        var rawMeasurements = new ConcurrentBag<RawMeasurement>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == MeterName && instrument is Histogram<long> histogram &&
                    instrument.Name is BatchSizeName or SubmissionSizeName)
                {
                    var boundaries = histogram.Advice?.HistogramBucketBoundaries?.ToArray();
                    published[instrument.Name] = new HistogramObservation(instrument, boundaries);
                    meterListener.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
        {
            string? batcherName = null;
            foreach (var tag in tags)
            {
                if (tag.Key == BatcherNameTag)
                {
                    batcherName = tag.Value as string;
                }
            }

            rawMeasurements.Add(new RawMeasurement(instrument.Name, batcherName, measurement));
        });
        listener.Start();

        // Resolve both batchers before the MeterProvider subscribes so it consumes the shared size instruments.
        var small = serviceProvider.GetRequiredKeyedService<IRequestBatcher<int, int>>("small");
        var large = serviceProvider.GetRequiredKeyedService<IRequestBatcher<int, int>>("large");
        var exporter = new SnapshotExporter();
        var reader = new BaseExportingMetricReader(exporter);
        using var meterProvider = Sdk.CreateMeterProviderBuilder()
            .AddMeter(MeterName)
            .AddReader(reader)
            .Build();

        // Force multiple Handler batches while retaining one submission observation.
        var smallRequests = Enumerable.Range(1000, 80).ToArray();
        var largeRequests = Enumerable.Range(2000, 120).ToArray();
        try
        {
            var smallResponses = await small.ExecuteAsync(smallRequests);
            var largeResponses = await large.ExecuteAsync(largeRequests,
                static (IReadOnlyList<RequestBatchEntry<int, int>> entries, InvocationCapture capture) =>
                {
                    capture.Record(entries, "large");
                    entries.SetResponses(static request => request * 2);
                });

            Assert.Equal(smallRequests.Select(static request => request + 1), smallResponses);
            Assert.Equal(largeRequests.Select(static request => request * 2), largeResponses);
            invocations.AssertExactlyOnce(smallRequests.Concat(largeRequests));

            AssertAdvice(published, BatchSizeName, _batchBounds);
            AssertAdvice(published, SubmissionSizeName, _submissionBounds);
            var smallBatches = invocations.GetBatchSizes("small");
            var largeBatches = invocations.GetBatchSizes("large");
            AssertBatchMeasurements(rawMeasurements, "small", smallBatches, 40, 80);
            AssertBatchMeasurements(rawMeasurements, "large", largeBatches, 120, 120);
            Assert.Equal(new[] { 80L, 120L }, rawMeasurements
                .Where(measurement => measurement.Name == SubmissionSizeName)
                .Select(measurement => measurement.Value)
                .Order());

            Assert.True(reader.Collect((int)TimeSpan.FromSeconds(10).TotalMilliseconds));
            AssertMetric(exporter, BatchSizeName, "small", _batchBounds, smallBatches);
            AssertMetric(exporter, BatchSizeName, "large", _batchBounds, largeBatches);
            AssertMetric(exporter, SubmissionSizeName, "small", _submissionBounds, [80], "multi");
            AssertMetric(exporter, SubmissionSizeName, "large", _submissionBounds, [120], "multi");
        }
        finally
        {
            await serviceProvider.DisposeAsync();
        }
    }

    private static void AssertAdvice(
        IReadOnlyDictionary<string, HistogramObservation> published,
        string instrumentName,
        long[] expectedBounds)
    {
        Assert.True(published.TryGetValue(instrumentName, out var observation),
            $"The {instrumentName} histogram was not published to MeterListener.");
        Assert.Equal(expectedBounds, observation!.Boundaries);
    }

    private static void AssertMetric(
        SnapshotExporter exporter,
        string metricName,
        string batcherName,
        long[] expectedBounds,
        long[] expectedValues,
        string? submissionKind = null)
    {
        var point = Assert.Single(exporter.Points, item =>
            item.Name == metricName && item.Tags[BatcherNameTag] == batcherName);
        Assert.Equal(expectedBounds.Select(static boundary => (double)boundary).Append(double.PositiveInfinity), point.Bounds);
        Assert.Equal(expectedValues.Length, point.Count);
        Assert.Equal(expectedValues.Sum(), point.Sum);
        Assert.Equal(expectedBounds.Length + 1, point.Buckets.Count);
        var lowerBound = double.NegativeInfinity;
        foreach (var upperBound in point.Bounds)
        {
            Assert.Equal(expectedValues.LongCount(value => value > lowerBound && value <= upperBound),
                point.Buckets[upperBound]);
            lowerBound = upperBound;
        }

        Assert.Equal(expectedValues.Length, point.Buckets.Values.Sum());
        Assert.Equal("Int32", point.Tags[RequestTypeTag]);
        Assert.Equal(submissionKind is null ? 2 : 3, point.Tags.Count);
        if (submissionKind is not null)
        {
            Assert.Equal(submissionKind, point.Tags[SubmissionKindTag]);
        }
    }

    private static void AssertBatchMeasurements(
        IEnumerable<RawMeasurement> measurements,
        string batcherName,
        long[] actualBatches,
        int maximumBatchSize,
        int submittedCount)
    {
        Assert.NotEmpty(actualBatches);
        Assert.All(actualBatches, size => Assert.InRange(size, 1, maximumBatchSize));
        Assert.Equal(submittedCount, actualBatches.Sum());
        Assert.Equal(actualBatches, measurements
            .Where(measurement => measurement.Name == BatchSizeName && measurement.BatcherName == batcherName)
            .Select(measurement => measurement.Value)
            .Order());
    }

    private sealed class SmallBatchHandler(InvocationCapture capture) : IRequestBatchHandler<int, int>
    {
        public ValueTask HandleAsync(
            IReadOnlyList<RequestBatchEntry<int, int>> entries,
            CancellationToken cancellationToken = default)
        {
            capture.Record(entries, "small");
            entries.SetResponses(static request => request + 1);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class InvocationCapture
    {
        private readonly ConcurrentDictionary<int, int> _counts = new();
        private readonly ConcurrentBag<(string BatcherName, long Size)> _batches = [];

        public void Record(IReadOnlyList<RequestBatchEntry<int, int>> entries, string batcherName)
        {
            _batches.Add((batcherName, entries.Count));
            foreach (var entry in entries)
            {
                _counts.AddOrUpdate(entry.Request, 1, static (_, count) => count + 1);
            }
        }

        public long[] GetBatchSizes(string batcherName) => _batches
            .Where(batch => batch.BatcherName == batcherName)
            .Select(batch => batch.Size)
            .Order()
            .ToArray();

        public void AssertExactlyOnce(IEnumerable<int> requests)
        {
            var expected = requests.ToHashSet();
            Assert.True(expected.SetEquals(_counts.Keys));
            Assert.All(_counts.Values, count => Assert.Equal(1, count));
        }
    }

    private sealed record HistogramObservation(Instrument Instrument, long[]? Boundaries);

    private sealed record RawMeasurement(string Name, string? BatcherName, long Value);

    private sealed class SnapshotExporter : BaseExporter<Metric>
    {
        public List<MetricSnapshot> Points { get; } = [];

        public override ExportResult Export(in Batch<Metric> batch)
        {
            Points.Clear();
            foreach (var metric in batch)
            {
                foreach (ref readonly var point in metric.GetMetricPoints())
                {
                    if (metric.MetricType == MetricType.Histogram)
                    {
                        var tags = new Dictionary<string, string>();
                        foreach (var tag in point.Tags)
                        {
                            tags.Add(tag.Key, tag.Value?.ToString() ?? "");
                        }

                        var buckets = new Dictionary<double, long>();
                        foreach (var bucket in point.GetHistogramBuckets())
                        {
                            buckets.Add(bucket.ExplicitBound, bucket.BucketCount);
                        }

                        Points.Add(new MetricSnapshot(
                            metric.Name,
                            tags,
                            point.GetHistogramCount(),
                            point.GetHistogramSum(),
                            buckets));
                    }
                }
            }

            return ExportResult.Success;
        }
    }

    private sealed record MetricSnapshot(
        string Name,
        Dictionary<string, string> Tags,
        long Count,
        double Sum,
        Dictionary<double, long> Buckets)
    {
        public double[] Bounds => Buckets.Keys.ToArray();
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class MetricsAdviceCollection
{
    public const string Name = "MetricsAdvice";
}
