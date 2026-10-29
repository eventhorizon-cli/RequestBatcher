using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using RequestBatcher.Diagnostics;

namespace RequestBatcher.Tests.Diagnostics;

[Collection("Tracing")]
public sealed class MetricsBucketTests
{
    private const string SubmissionSize = "requestbatcher.submission.size";
    private const string BatchSize = "requestbatcher.batch.size";

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void SizeHistograms_SmallBatchSize_PreserveEveryIntegerBoundary(int batchSize)
    {
        using var capture = new Capture(services => Register(services, batchSize));
        RequestBatcherMetrics.RecordSubmissionSize("Int32", null, "multi", 0);
        RequestBatcherMetrics.RecordSubmissionSize("Int32", null, "multi", batchSize);
        RecordBatch(batchSize);
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
    public void SizeHistograms_BatchSize100_ExportExplicitBucketsAndExactTotals()
    {
        using var capture = new Capture(services => Register(services, 100));
        foreach (var size in new[] { 0, 1, 3, 100, 101, 3201 })
        {
            RequestBatcherMetrics.RecordSubmissionSize("Int32", null, "multi", size);
        }
        foreach (var size in new[] { 1, 3, 100 })
        {
            RecordBatch(size);
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
    public void SizeHistograms_MaximumBatchSize_AvoidOverflowWithoutResolvingPipeline()
    {
        using var capture = new Capture(services => Register(services, int.MaxValue));
        RequestBatcherMetrics.RecordSubmissionSize("Int32", null, "multi", int.MaxValue);
        RecordBatch(int.MaxValue);
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
    public void Instrumentation_AllRegistrationShapes_ReadsOptionsWithoutResolvingHandlers(int shape, bool instrumentationFirst)
    {
        using var capture = new Capture(services =>
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
        RecordBatch(2);
        capture.Collect();
        Assert.Equal(new double[] { 0, 1, 2, 3, double.PositiveInfinity }, capture.Get(BatchSize).Bounds);
    }

    [Fact]
    public void Instrumentation_NamedPipelinesAndRepeatedRegistration_UsesMaximumBatchSizeSchema()
    {
        using var capture = new Capture(services =>
        {
            services.AddRequestBatcher<int, UnresolvedHandler>("small", ServiceLifetime.Scoped, options => options.BatchSize = 40);
            services.AddRequestBatcher<int, int, UnresolvedResponseHandler>("large", ServiceLifetime.Scoped, options => options.BatchSize = 120);
            services.AddDelegateRequestBatcher<int>("same-size", options => options.BatchSize = 40);
        }, repeatInstrumentation: true);
        RequestBatcherMetrics.RecordSubmissionSize("Int32", "small", "multi", 40);
        RequestBatcherMetrics.RecordSubmissionSize("Int32", "large", "multi", 120);
        using (RequestBatcherMetrics.StartBatch("Int32", "small", 40))
        {
        }
        using (RequestBatcherMetrics.StartBatch("Int32", "large", 120))
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
    public void Instrumentation_ManyDifferentBatchSizes_KeepsSameBucketBudgetAsMaximumAlone()
    {
        using var maximumOnly = new Capture(services => Register(services, 120));
        using var many = new Capture(services =>
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
            RequestBatcherMetrics.RecordSubmissionSize("Int32", null, "multi", size);
            RecordBatch(size);
        }
        maximumOnly.Collect();
        many.Collect();

        foreach (var instrument in new[] { BatchSize, SubmissionSize })
        {
            var expected = maximumOnly.Get(instrument);
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
    public void Instrumentation_ProvidersWithDifferentOptions_KeepIndependentSchemas()
    {
        using var first = new Capture(services => Register(services, 1));
        using var second = new Capture(services => Register(services, 3));
        RecordBatch(1);
        first.Collect();
        second.Collect();
        Assert.Equal(new double[] { 0, 1, double.PositiveInfinity }, first.Get(BatchSize).Bounds);
        Assert.Equal(new double[] { 0, 1, 2, 3, double.PositiveInfinity }, second.Get(BatchSize).Bounds);
        Assert.Equal(1, first.Get(BatchSize).Count);
        Assert.Equal(1, second.Get(BatchSize).Count);
    }

    [Fact]
    public async Task Instrumentation_NoPipelineDirectMeterAndUnrelatedInstruments_RetainSdkDefaults()
    {
        using var configured = new Capture(services => Register(services, 3));
        using var noPipeline = new Capture();
        using var direct = new Capture(services => Register(services, 100), directMeter: true);
        using var foreign = new Meter("BucketTests.Foreign");
        foreign.CreateHistogram<long>(BatchSize).Record(3);
        foreign.CreateHistogram<long>(SubmissionSize).Record(3);
        RequestBatcherMetrics.RecordSubmissionSize("Int32", null, "multi", 3);
        RecordBatch(3);
        await RequestBatcherMetrics.MeasureExecution("Int32", null, "single", 0, static _ => Task.CompletedTask);
        configured.Collect();
        noPipeline.Collect();
        direct.Collect();

        foreach (var instrument in new[] { BatchSize, SubmissionSize })
        {
            var defaults = direct.Get(instrument).Bounds;
            Assert.Equal(defaults, noPipeline.Get(instrument).Bounds);
            Assert.Equal(defaults, configured.Get(instrument, "BucketTests.Foreign").Bounds);
            Assert.False(defaults.SequenceEqual(configured.Get(instrument).Bounds));
        }
        foreach (var duration in new[] { "requestbatcher.handle.duration", "requestbatcher.execute.duration" })
        {
            Assert.Equal(direct.Get(duration).Bounds, configured.Get(duration).Bounds);
            Assert.Equal(new double[] { 0, 5, 10, 25, 50, 75, 100, 250, 500, 750, 1000, 2500, 5000, 7500, 10000, double.PositiveInfinity }, configured.Get(duration).Bounds);
            Assert.Equal(1, configured.Get(duration).Count);
        }
    }

    private static void Register(IServiceCollection services, int batchSize) =>
        services.AddRequestBatcher<int, UnresolvedHandler>(ServiceLifetime.Scoped, options => options.BatchSize = batchSize);

    private static void RecordBatch(int size)
    {
        using var scope = RequestBatcherMetrics.StartBatch("Int32", null, size);
    }

    private sealed class Capture : IDisposable
    {
        private readonly BaseExportingMetricReader _reader;
        private readonly MeterProvider _provider;

        public Capture(
            Action<IServiceCollection>? register = null,
            bool instrumentationFirst = false,
            bool repeatInstrumentation = false,
            bool directMeter = false)
        {
            _reader = new BaseExportingMetricReader(Exporter);
            var builder = Sdk.CreateMeterProviderBuilder().AddMeter("BucketTests.Foreign").AddReader(_reader);
            if (instrumentationFirst)
            {
                builder.AddRequestBatcherInstrumentation();
            }
            if (register is not null)
            {
                builder.ConfigureServices(register);
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
        public void Collect() => Assert.True(_reader.Collect(10000));
        public Snapshot Get(string name, string meter = "RequestBatcher") =>
            Assert.Single(Exporter.Points, point => point.Name == name && point.Meter == meter);
        public void Dispose() => _provider.Dispose();
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
