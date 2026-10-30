using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using RequestBatcher.Diagnostics.Metrics;

namespace RequestBatcher.Tests.Diagnostics;

[Collection("Tracing")]
public sealed class MetricsBucketTests
{
    private const string SubmissionSize = "requestbatcher.submission.size";
    private const string BatchSize = "requestbatcher.batch.size";

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task SizeHistograms_SmallBatchSize_PreserveEveryIntegerBoundary(int batchSize)
    {
        await using var capture = new Capture(services => Register(services, batchSize));
        capture.Metrics.RecordSubmissionSize("Int32", null, "multi", 0);
        capture.Metrics.RecordSubmissionSize("Int32", null, "multi", batchSize);
        RecordBatch(capture.Metrics, batchSize);
        capture.Collect();

        var batch = capture.Get(BatchSize);
        Assert.Equal(batchSize == 1 ? new double[] { 0, 1, double.PositiveInfinity } : [0, 1, 2, 3, double.PositiveInfinity], batch.Bounds);
        Assert.Equal(1, batch.Count);
        Assert.Equal(batchSize, batch.Sum);
        Assert.Equal(1, batch.Buckets[batchSize]);
        var submission = capture.Get(SubmissionSize);
        Assert.Equal(batchSize == 1 ? new double[] { 0, 1, 2, 4, 8, 16, 32, double.PositiveInfinity } : [0, 1, 2, 3, 6, 12, 24, 48, 96, double.PositiveInfinity], submission.Bounds);
        Assert.Equal(2, submission.Count);
        Assert.Equal(batchSize, submission.Sum);
        Assert.Equal(1, submission.Buckets[0]);
        Assert.Equal(1, submission.Buckets[batchSize]);
    }

    [Fact]
    public async Task SizeHistograms_BatchSize100_ExportExplicitBucketsAndExactTotals()
    {
        await using var capture = new Capture(services => Register(services, 100));
        foreach (var size in new[] { 0, 1, 3, 100, 101, 3201 })
        {
            capture.Metrics.RecordSubmissionSize("Int32", null, "multi", size);
        }
        foreach (var size in new[] { 1, 3, 100 })
        {
            RecordBatch(capture.Metrics, size);
        }
        capture.Collect();

        var batch = capture.Get(BatchSize);
        Assert.Equal(new double[] { 0, 1, 5, 10, 14, 19, 23, 28, 32, 37, 41, 46, 50, 55, 60, 64, 69, 73, 78, 82, 87, 91, 96, 100, double.PositiveInfinity }, batch.Bounds);
        Assert.Equal(3, batch.Count);
        Assert.Equal(104, batch.Sum);
        Assert.Equal(1, batch.Buckets[1]);
        Assert.Equal(1, batch.Buckets[5]);
        Assert.Equal(1, batch.Buckets[100]);

        var submission = capture.Get(SubmissionSize);
        Assert.Equal(new double[] { 0, 1, 6, 12, 18, 24, 30, 36, 42, 48, 53, 59, 65, 71, 77, 83, 89, 95, 100, 200, 400, 800, 1600, 3200, double.PositiveInfinity }, submission.Bounds);
        Assert.Equal(6, submission.Count);
        Assert.Equal(3406, submission.Sum);
        Assert.Equal(6, submission.Buckets.Values.Sum());
        foreach (var boundary in new double[] { 0, 1, 6, 100, 200, double.PositiveInfinity })
        {
            Assert.Equal(1, submission.Buckets[boundary]);
        }
    }

    [Fact]
    public async Task SizeHistograms_MaximumBatchSize_AvoidOverflowWithoutResolvingBatcher()
    {
        await using var capture = new Capture(services => Register(services, int.MaxValue));
        capture.Metrics.RecordSubmissionSize("Int32", null, "multi", int.MaxValue);
        RecordBatch(capture.Metrics, int.MaxValue);
        capture.Collect();

        var batch = capture.Get(BatchSize);
        Assert.Equal(25, batch.Bounds.Length);
        Assert.Equal(new double[] { 0, 1, 97612894 }, batch.Bounds.Take(3));
        Assert.Contains(1073741824d, batch.Bounds);
        Assert.Equal(int.MaxValue, batch.Bounds[^2]);
        Assert.Equal(double.PositiveInfinity, batch.Bounds[^1]);
        Assert.Equal(1, batch.Buckets[int.MaxValue]);
        var submission = capture.Get(SubmissionSize);
        Assert.Equal(new double[] { 4294967294, 8589934588, 17179869176, 34359738352, 68719476704, double.PositiveInfinity }, submission.Bounds.TakeLast(6));
        Assert.Equal(25, submission.Bounds.Length);
        Assert.Equal(1, submission.Count);
        Assert.Equal(int.MaxValue, submission.Sum);
        Assert.Equal(submission.Bounds.Order().Distinct(), submission.Bounds);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(3, true)]
    public async Task Instrumentation_AllRegistrationShapes_ReadsOptionsWithoutResolvingHandlers(int shape, bool instrumentationFirst)
    {
        await using var capture = new Capture(services =>
        {
            switch (shape)
            {
                case 0:
                    services.AddRequestBatcher<int, UnresolvedHandler>(ServiceLifetime.Scoped, options => options.BatchSize = 3);
                    break;
                case 1:
                    services.AddRequestBatcher<int, int, UnresolvedResponseHandler>(ServiceLifetime.Transient, options => options.BatchSize = 3);
                    break;
                case 2:
                    services.AddDelegateRequestBatcher<int>(options => options.BatchSize = 3);
                    break;
                case 3:
                    services.AddDelegateRequestBatcher<int, int>(options => options.BatchSize = 3);
                    break;
            }
        }, instrumentationFirst: instrumentationFirst);
        RecordBatch(capture.Metrics, 2);
        capture.Collect();
        Assert.Equal(new double[] { 0, 1, 2, 3, double.PositiveInfinity }, capture.Get(BatchSize).Bounds);
    }

    [Fact]
    public async Task Instrumentation_NamedBatchersAndRepeatedRegistration_UsesMaximumBatchSizeSchema()
    {
        await using var capture = new Capture(services =>
        {
            services.AddRequestBatcher<int, UnresolvedHandler>("small", ServiceLifetime.Scoped, options => options.BatchSize = 40);
            services.AddRequestBatcher<int, int, UnresolvedResponseHandler>("large", ServiceLifetime.Scoped, options => options.BatchSize = 120);
            services.AddDelegateRequestBatcher<int>("same-size", options => options.BatchSize = 40);
        }, repeatInstrumentation: true);
        capture.Metrics.RecordSubmissionSize("Int32", "small", "multi", 40);
        capture.Metrics.RecordSubmissionSize("Int32", "large", "multi", 120);
        using (capture.Metrics.StartBatch("Int32", "small", 40))
        {
        }
        using (capture.Metrics.StartBatch("Int32", "large", 120))
        {
        }
        capture.Collect();

        var batches = capture.Exporter.Points.Where(point => point.Name == BatchSize).ToArray();
        Assert.Equal(2, batches.Length);
        Assert.All(batches, point => Assert.Equal(new double[] { 0, 1, 6, 11, 17, 22, 28, 33, 39, 44, 50, 55, 60, 66, 71, 77, 82, 88, 93, 99, 104, 110, 115, 120, double.PositiveInfinity }, point.Bounds));
        Assert.Equal(2, batches.Sum(point => point.Count));
        Assert.Equal(160, batches.Sum(point => point.Sum));
        Assert.Equal(1, batches.Sum(point => point.Buckets[44]));
        Assert.Equal(1, batches.Sum(point => point.Buckets[120]));
        var submissions = capture.Exporter.Points.Where(point => point.Name == SubmissionSize).ToArray();
        Assert.Equal(2, submissions.Length);
        Assert.All(submissions, point => Assert.Equal(new double[] { 0, 1, 8, 15, 22, 29, 36, 43, 50, 57, 64, 71, 78, 85, 92, 99, 106, 113, 120, 240, 480, 960, 1920, 3840, double.PositiveInfinity }, point.Bounds));
        Assert.Equal(2, submissions.Sum(point => point.Count));
        Assert.Equal(160, submissions.Sum(point => point.Sum));
        Assert.Equal(1, submissions.Sum(point => point.Buckets[43]));
        Assert.Equal(1, submissions.Sum(point => point.Buckets[120]));
    }

    [Fact]
    public async Task Instrumentation_ManyDifferentBatchSizes_KeepsSameBucketBudgetAsMaximumAlone()
    {
        Snapshot[] expectedPoints;
        await using (var maximumOnly = new Capture(services => Register(services, 120)))
        {
            foreach (var size in new[] { 40, 120 })
            {
                maximumOnly.Metrics.RecordSubmissionSize("Int32", null, "multi", size);
                RecordBatch(maximumOnly.Metrics, size);
            }
            maximumOnly.Collect();
            expectedPoints = maximumOnly.Exporter.Points.ToArray();
        }
        await using var many = new Capture(services =>
        {
            for (var size = 1; size <= 120; size++)
            {
                var configuredSize = size;
                services.AddRequestBatcher<int, UnresolvedHandler>($"size-{size}", ServiceLifetime.Scoped,
                    options => options.BatchSize = configuredSize);
            }
        });
        foreach (var size in new[] { 40, 120 })
        {
            many.Metrics.RecordSubmissionSize("Int32", null, "multi", size);
            RecordBatch(many.Metrics, size);
        }
        many.Collect();

        foreach (var instrument in new[] { BatchSize, SubmissionSize })
        {
            var expected = Assert.Single(expectedPoints, point => point.Name == instrument);
            var actual = many.Get(instrument);
            Assert.Equal(expected.Bounds, actual.Bounds);
            Assert.Equal(25, actual.Bounds.Length);
            Assert.Equal(2, actual.Count);
            Assert.Equal(160, actual.Sum);
            Assert.Equal(expected.Count, actual.Count);
            Assert.Equal(expected.Sum, actual.Sum);
            Assert.Equal(expected.Buckets, actual.Buckets);
        }
    }

    [Fact]
    public async Task Instrumentation_MetersWithDifferentOptions_KeepIndependentAdvice()
    {
        await using var first = new Capture(services => Register(services, 1));
        await using var second = new Capture(services => Register(services, 3));
        var instruments = new List<Histogram<long>>();
        var measurements = new List<long>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == "RequestBatcher" && instrument.Name == BatchSize &&
                    instrument is Histogram<long> histogram)
                {
                    instruments.Add(histogram);
                    meterListener.EnableMeasurementEvents(histogram);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => measurements.Add(value));
        listener.Start();

        Assert.Equal(2, instruments.Count);
        Assert.NotSame(instruments[0], instruments[1]);
        Assert.Contains(instruments, instrument => instrument.Advice!.HistogramBucketBoundaries!.SequenceEqual(new long[] { 0, 1 }));
        Assert.Contains(instruments, instrument => instrument.Advice!.HistogramBucketBoundaries!.SequenceEqual(new long[] { 0, 1, 2, 3 }));

        RecordBatch(first.Metrics, 1);
        await first.DisposeAsync();
        RecordBatch(first.Metrics, 1);
        RecordBatch(second.Metrics, 3);
        Assert.Equal(new long[] { 1, 3 }, measurements);
    }

    [Fact]
    public async Task SizeHistograms_WithMeterListener_ExposeAdviceAndRawMeasurements()
    {
        await using var metrics = new RequestBatcherMetrics([new(120)]);
        var observations = new Dictionary<string, (long[] Advice, List<long> Values)>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == RequestBatcherMetrics.MeterName &&
                    instrument.Name is BatchSize or SubmissionSize && instrument is Histogram<long> histogram)
                {
                    observations[instrument.Name] = (histogram.Advice!.HistogramBucketBoundaries!.ToArray(), []);
                    meterListener.EnableMeasurementEvents(histogram);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, _, _) => observations[instrument.Name].Values.Add(value));
        listener.Start();

        metrics.RecordSubmissionSize("Int32", null, "multi", 121);
        using (metrics.StartBatch("Int32", null, 39))
        {
        }

        Assert.Equal(24, observations[BatchSize].Advice.Length);
        Assert.Equal(24, observations[SubmissionSize].Advice.Length);
        Assert.Equal(new long[] { 39 }, observations[BatchSize].Values);
        Assert.Equal(new long[] { 121 }, observations[SubmissionSize].Values);
    }

    [Fact]
    public async Task SizeHistograms_WithExplicitView_OverrideCoreAdvice()
    {
        await using var metrics = new RequestBatcherMetrics([new(120)]);
        var exporter = new SnapshotExporter();
        var reader = new BaseExportingMetricReader(exporter);
        using var provider = Sdk.CreateMeterProviderBuilder()
            .AddMeter(RequestBatcherMetrics.MeterName)
            .AddView(BatchSize, new ExplicitBucketHistogramConfiguration { Boundaries = [0, 1, 2] })
            .AddReader(reader)
            .Build();

        using (metrics.StartBatch("Int32", null, 3))
        {
        }
        Assert.True(reader.Collect(10000));

        var point = Assert.Single(exporter.Points, item => item.Name == BatchSize);
        Assert.Equal(new double[] { 0, 1, 2, double.PositiveInfinity }, point.Bounds);
        Assert.Equal(1, point.Count);
        Assert.Equal(3, point.Sum);
        Assert.Equal(1, point.Buckets[double.PositiveInfinity]);
    }

    [Fact]
    public async Task Instrumentation_DirectMeter_UsesAdviceAndRetainsOtherDefaults()
    {
        await using var direct = new Capture(services => Register(services, 3), directMeter: true);
        using var foreign = new Meter("BucketTests.Foreign");
        foreign.CreateHistogram<long>(BatchSize).Record(3);
        foreign.CreateHistogram<long>(SubmissionSize).Record(3);
        direct.Metrics.RecordSubmissionSize("Int32", null, "multi", 3);
        RecordBatch(direct.Metrics, 3);
        await direct.Metrics.MeasureExecution("Int32", null, "single", 0, static _ => Task.CompletedTask);
        direct.Collect();

        double[] defaults = [0, 5, 10, 25, 50, 75, 100, 250, 500, 750, 1000, 2500, 5000, 7500, 10000, double.PositiveInfinity];
        foreach (var instrument in new[] { BatchSize, SubmissionSize })
        {
            Assert.Equal(defaults, direct.Get(instrument, "BucketTests.Foreign").Bounds);
            Assert.False(defaults.SequenceEqual(direct.Get(instrument).Bounds));
        }
        Assert.Equal(new double[] { 0, 1, 2, 3, double.PositiveInfinity }, direct.Get(BatchSize).Bounds);
        Assert.Equal(new double[] { 0, 1, 2, 3, 6, 12, 24, 48, 96, double.PositiveInfinity }, direct.Get(SubmissionSize).Bounds);
        foreach (var duration in new[] { "requestbatcher.handle.duration", "requestbatcher.execute.duration" })
        {
            Assert.Equal(defaults, direct.Get(duration).Bounds);
            Assert.Equal(1, direct.Get(duration).Count);
        }
    }

    private static void Register(IServiceCollection services, int batchSize) =>
        services.AddRequestBatcher<int, UnresolvedHandler>(ServiceLifetime.Scoped, options => options.BatchSize = batchSize);

    private static void RecordBatch(RequestBatcherMetrics metrics, int size)
    {
        using var scope = metrics.StartBatch("Int32", null, size);
    }

    private sealed class Capture : IAsyncDisposable
    {
        private readonly BaseExportingMetricReader _reader;
        private readonly MeterProvider _provider;
        private readonly ServiceProvider _services;

        public Capture(
            Action<IServiceCollection> register,
            bool instrumentationFirst = false,
            bool repeatInstrumentation = false,
            bool directMeter = false)
        {
            var services = new ServiceCollection();
            register(services);
            _services = services.BuildServiceProvider();
            Metrics = _services.GetRequiredService<RequestBatcherMetrics>();

            _reader = new BaseExportingMetricReader(Exporter);
            var builder = Sdk.CreateMeterProviderBuilder().AddMeter("BucketTests.Foreign").AddReader(_reader);
            if (instrumentationFirst)
            {
                builder.AddRequestBatcherInstrumentation();
            }
            if (directMeter)
            {
                builder.AddMeter("RequestBatcher");
            }
            else if (!instrumentationFirst || repeatInstrumentation)
            {
                builder.AddRequestBatcherInstrumentation();
            }
            if (repeatInstrumentation)
            {
                builder.AddRequestBatcherInstrumentation();
            }
            _provider = builder.Build();
        }

        public SnapshotExporter Exporter { get; } = new();
        public RequestBatcherMetrics Metrics { get; }
        public void Collect() => Assert.True(_reader.Collect(10000));
        public Snapshot Get(string name, string meter = "RequestBatcher") =>
            Assert.Single(Exporter.Points, point => point.Name == name && point.Meter == meter);
        public async ValueTask DisposeAsync()
        {
            _provider.Dispose();
            await _services.DisposeAsync();
        }
    }

    private sealed class SnapshotExporter : BaseExporter<Metric>
    {
        public List<Snapshot> Points { get; } = new();

        public override ExportResult Export(in Batch<Metric> batch)
        {
            Points.Clear();
            foreach (var metric in batch)
            {
                foreach (ref readonly var point in metric.GetMetricPoints())
                {
                    var buckets = new Dictionary<double, long>();
                    foreach (var bucket in point.GetHistogramBuckets())
                    {
                        buckets.Add(bucket.ExplicitBound, bucket.BucketCount);
                    }
                    Points.Add(new Snapshot(metric.MeterName, metric.Name, point.GetHistogramCount(), point.GetHistogramSum(), buckets));
                }
            }
            return ExportResult.Success;
        }
    }

    private sealed record Snapshot(string Meter, string Name, long Count, double Sum, Dictionary<double, long> Buckets)
    {
        public double[] Bounds => Buckets.Keys.ToArray();
    }

    private sealed class UnresolvedHandler : IRequestBatchHandler<int>
    {
        public UnresolvedHandler() => throw new InvalidOperationException("Metrics configuration must not resolve a Handler.");
        public ValueTask HandleAsync(IReadOnlyList<int> requests, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("No requests should be dispatched by a bucket configuration test.");
    }

    private sealed class UnresolvedResponseHandler : IRequestBatchHandler<int, int>
    {
        public UnresolvedResponseHandler() => throw new InvalidOperationException("Metrics configuration must not resolve a Handler.");
        public ValueTask HandleAsync(IReadOnlyList<RequestBatchEntry<int, int>> entries, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("No requests should be dispatched by a bucket configuration test.");
    }
}
