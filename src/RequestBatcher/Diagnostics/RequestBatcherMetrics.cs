using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace RequestBatcher.Diagnostics;

/// <summary>
/// Records low-cardinality RequestBatcher metrics when a meter listener is present.
/// </summary>
internal static class RequestBatcherMetrics
{
    internal const string MeterName = "RequestBatcher";

    private static readonly Meter _meter = new(MeterName);
    private static readonly Counter<long> _submittedRequests = _meter.CreateCounter<long>(
        "requestbatcher.requests.submitted",
        unit: "{request}",
        description: "Number of requests accepted by a RequestBatcher coordinator.");
    private static readonly Counter<long> _processedRequests = _meter.CreateCounter<long>(
        "requestbatcher.requests.processed",
        unit: "{request}",
        description: "Number of requests completed by a RequestBatcher handler.");
    private static readonly Counter<long> _processedBatches = _meter.CreateCounter<long>(
        "requestbatcher.batches.processed",
        unit: "{batch}",
        description: "Number of non-empty batches dispatched to a RequestBatcher handler.");
    private static readonly Histogram<long> _batchSize = _meter.CreateHistogram<long>(
        "requestbatcher.batch.size",
        unit: "{request}",
        description: "Number of requests in each dispatched batch.");
    private static readonly Histogram<double> _batchDuration = _meter.CreateHistogram<double>(
        "requestbatcher.batch.duration",
        unit: "ms",
        description: "Handler duration for each dispatched batch.");

    internal static void RecordSubmitted(string requestTypeName, string? batcherName, int count)
    {
        // Avoid preparing tags when no listener subscribes to this instrument.
        if (!_submittedRequests.Enabled)
        {
            return;
        }

        Add(_submittedRequests, count, requestTypeName, batcherName);
    }

    internal static BatchScope StartBatch(string requestTypeName, string? batcherName, int count)
    {
        // Skip tag preparation and timing state when none of the batch instruments has subscribers.
        if (!_processedBatches.Enabled && !_processedRequests.Enabled && !_batchSize.Enabled && !_batchDuration.Enabled)
        {
            return default;
        }

        return new BatchScope(requestTypeName, batcherName, count, Stopwatch.GetTimestamp());
    }

    private static void Add(Counter<long> instrument, long value, string requestTypeName, string? batcherName)
    {
        var tags = new TagList { { "requestbatcher.request.type", requestTypeName } };
        if (batcherName is not null)
        {
            tags.Add("requestbatcher.name", batcherName);
        }

        instrument.Add(value, tags);
    }

    internal readonly struct BatchScope : IDisposable
    {
        private readonly string? _requestTypeName;
        private readonly string? _batcherName;
        private readonly int _count;
        private readonly long _startedAt;
        private readonly bool _enabled;

        internal BatchScope(string requestTypeName, string? batcherName, int count, long startedAt)
        {
            _requestTypeName = requestTypeName;
            _batcherName = batcherName;
            _count = count;
            _startedAt = startedAt;
            _enabled = true;
        }

        public void Dispose()
        {
            if (!_enabled)
            {
                return;
            }

            var tags = new TagList { { "requestbatcher.request.type", _requestTypeName! } };
            if (_batcherName is not null)
            {
                tags.Add("requestbatcher.name", _batcherName);
            }

            // Enabled reflects each instrument's listener subscriptions, which can differ within the same Meter.
            // These guards skip unsubscribed recording calls; the instruments also handle no subscribers internally.
            if (_processedBatches.Enabled)
            {
                _processedBatches.Add(1, tags);
            }

            if (_processedRequests.Enabled)
            {
                _processedRequests.Add(_count, tags);
            }

            if (_batchSize.Enabled)
            {
                _batchSize.Record(_count, tags);
            }

            // Keep elapsed-time calculation inside the guard so it runs only when duration is collected.
            if (_batchDuration.Enabled)
            {
                _batchDuration.Record(Stopwatch.GetElapsedTime(_startedAt).TotalMilliseconds, tags);
            }
        }
    }
}
