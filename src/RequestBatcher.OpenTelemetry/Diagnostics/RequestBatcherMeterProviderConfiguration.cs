using global::OpenTelemetry.Metrics;
using Microsoft.Extensions.DependencyInjection;
using RequestBatcher.Diagnostics;

namespace RequestBatcher.OpenTelemetry.Diagnostics;

/// <summary>
/// Builds shared size buckets from pipeline snapshots after application registration is complete.
/// </summary>
internal sealed class RequestBatcherMeterProviderConfiguration
{
    private const int MaximumBucketCount = 25;
    private const int SubmissionDoublings = 5;

    public void ConfigureBuilder(IServiceProvider serviceProvider, MeterProviderBuilder meterProviderBuilder)
    {
        var maximumBatchSize = serviceProvider.GetServices<RequestBatcherMetricsConfiguration>()
            .Select(configuration => configuration.BatchSize)
            .DefaultIfEmpty()
            .Max();
        if (maximumBatchSize == 0)
        {
            return;
        }

        // Bound the number of bucket series independently of pipeline count.
        // Reserve boundaries for zero, one, and the SDK's overflow bucket.
        var batchBounds = CreateSizeBounds(maximumBatchSize, MaximumBucketCount - 3);
        var submissionBounds = CreateSizeBounds(maximumBatchSize, MaximumBucketCount - 3 - SubmissionDoublings);
        for (var doubling = 1; doubling <= SubmissionDoublings; doubling++)
        {
            submissionBounds.Add((double)maximumBatchSize * (1 << doubling));
        }

        var batchBuckets = new ExplicitBucketHistogramConfiguration { Boundaries = batchBounds.ToArray() };
        var submissionBuckets = new ExplicitBucketHistogramConfiguration { Boundaries = submissionBounds.ToArray() };
        meterProviderBuilder.AddView(instrument => instrument.Meter.Name != RequestBatcherMetrics.MeterName
            ? null
            : instrument.Name switch
            {
                RequestBatcherMetricNames.BatchSize => batchBuckets,
                RequestBatcherMetricNames.SubmissionSize => submissionBuckets,
                _ => null,
            });
    }

    private static SortedSet<double> CreateSizeBounds(int maximumBatchSize, int steps)
    {
        var bounds = new SortedSet<double> { 0, 1 };
        for (var step = 1; step <= steps; step++)
        {
            bounds.Add(Math.Ceiling((double)maximumBatchSize * step / steps));
        }

        return bounds;
    }
}
