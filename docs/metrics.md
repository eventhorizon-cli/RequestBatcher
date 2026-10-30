# RequestBatcher Metrics

RequestBatcher exposes four histograms and a backlog gauge through the `RequestBatcher` Meter. This guide covers their recording boundaries,
tags, bucket advice, and query examples. See the [Chinese guide](metrics.zh-CN.md).

## Registration

For metrics alone, register the Meter directly:

```csharp
builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics => metrics.AddMeter("RequestBatcher"));
```

Alternatively, install `RequestBatcher.OpenTelemetry` and use
`.WithMetrics(metrics => metrics.AddRequestBatcherInstrumentation())`. Both register the Meter and use the size-histogram
bucket advice supplied by core. Register all batchers before building the service provider. When the first batcher is
resolved, the shared core metrics service uses the largest registered `BatchSize` to create immutable advice; it does not
resolve other batchers or handlers. Each container owns its metrics service and disposes it after its batchers drain.
Independent containers may publish different bucket layouts; do not combine incompatible layouts in one metric stream.
Once the container disposes the metrics service, calls through retained batcher references emit no further measurements.

## Instruments

| Instrument | Type | Unit | Meaning |
| --- | --- | --- | --- |
| `requestbatcher.submission.size` | `Histogram<long>` | `{request}` | Request items in each submission reaching coordinator admission. |
| `requestbatcher.batch.size` | `Histogram<long>` | `{request}` | Requests per completed Handler batch, including failed attempts; items canceled before dispatch are excluded. |
| `requestbatcher.handle.duration` | `Histogram<double>` | `ms` | Handler batch duration, including Handler scope disposal. |
| `requestbatcher.execute.duration` | `Histogram<double>` | `ms` | End-to-end duration of one public `ExecuteAsync` call, including capacity waiting, queueing, all Handler batches, and response assembly. |
| `requestbatcher.backlog` | `ObservableGauge<long>` | `{request}` | Approximate enqueued items not yet pulled; excludes capacity waits and active Handler work. |

Histogram `sum` accumulates measurements, `count` counts observations, and buckets describe their distribution; `sum / count`
gives the mean. OTLP exports sum and count as fields of the same Histogram data point.

`submission.size` records once for each submission reaching coordinator admission, using the materialized item count,
including zero for an empty collection. Pre-canceled calls, calls after stop, null collections, and materialization failures
rejected before admission are excluded; queue-full and later failures remain included. Capacity slices and Handler batches
do not add observations. `batch.size` records each completed Handler batch, including failed attempts; requests canceled
before dispatch are excluded. `handle.duration` covers Handler execution and scope disposal per batch. `execute.duration`
measures the caller-visible operation. For P95 of `execute.duration`, filter `requestbatcher.outcome=success` so fast
failures and cancellations do not lower the latency.

All instruments include the short request type (`requestbatcher.request.type`) and optional batcher name
(`requestbatcher.name`). Types with the same short name share a dimension; use batcher names to distinguish pipelines.
`submission.size` adds `requestbatcher.submission.kind` (`single` or `multi`), without an outcome tag. `execute.duration`
also records `requestbatcher.submission.kind` and `requestbatcher.outcome` (`success`, `error`, or `canceled`). Empty,
failed, and canceled calls can be measured; a null collection argument rejected by synchronous validation is not measured.
Delegate submissions include Handler binding in the measured operation; binding failures record `error`. Delegate
extension argument validation and unsupported-batcher checks occur before measurement.
When subscribed, each empty, failed, or canceled call records once. Outcome follows the returned Task's final status, not
the exception type. Request payloads, tenant values, and partition keys are not recorded. `single` identifies the
single-request overload; `multi` identifies the collection overload, including empty or one-item collections. These tags
do not indicate how many Handler batches processed a submission.

## Queue backlog

`requestbatcher.backlog` reads the Batcher's consumer snapshot when a metrics reader collects. It includes all assigned
partitions and excludes requests still waiting to enter the queue and batches already pulled for Handler execution.
Canceled entries remain counted until pulled and skipped. Each successful collection reports the current approximate
value, including zero. If statistics are unavailable, the observation is omitted rather than replaced with zero.

The reader controls collection timing: periodic OpenTelemetry readers, pull exporters, and native `MeterListener`
all work without a RequestBatcher timer. Short peaks between collections may be missed. The gauge has the request-type
and optional Batcher-name tags, with no submission-kind, outcome, or partition tags. It remains observable during
draining and is unregistered when the coordinator is disposed. An in-progress collection can finish during disposal.
Unlike the histograms, the gauge has no buckets and is not a cumulative total; read its value directly instead of
applying `rate`. OTLP keeps `requestbatcher.backlog`; the sample's Prometheus query is `requestbatcher_backlog`.

## Export Names and Queries

OTLP retains dotted names such as `requestbatcher.execute.duration` and `requestbatcher.submission.kind`. The sample's
Prometheus translation exports size histograms as `requestbatcher_submission_size_sum`, `_count`, and `_bucket` (likewise
for `requestbatcher_batch_size`), and durations as `requestbatcher_execute_duration_milliseconds_sum`, `_count`, and
`_bucket` (likewise for `requestbatcher_handle_duration_milliseconds`). Labels use underscores and tag values stay
unchanged. Exporter naming and histogram translation can differ.

To estimate successful collection-submission P95 latency (ms):

```promql
histogram_quantile(0.95,
  sum by (le, requestbatcher_name, requestbatcher_request_type) (
    rate(requestbatcher_execute_duration_milliseconds_bucket{
      requestbatcher_submission_kind="multi", requestbatcher_outcome="success"
    }[5m])
  )
)
```

For the same time range, request type, and batcher name (summing submission kinds), the `submission.size` sum is admitted
request items and the `batch.size` sum is Handler-consumed requests. The `batch.size` and `handle.duration` counts are
completed Handler batches. The `execute.duration` count is completed public calls, including pre-admission rejections,
not submitted items.

| Measure | Prometheus expression |
| --- | --- |
| Estimated admitted request items in the last minute | `increase(requestbatcher_submission_size_sum[1m])` |
| Estimated admitted submissions in the last minute | `increase(requestbatcher_submission_size_count[1m])` |
| Admitted submissions/s | `rate(requestbatcher_submission_size_count[5m])` |
| Handler-consumed request items/s | `rate(requestbatcher_batch_size_sum[5m])` |
| Completed Handler batches/s | `rate(requestbatcher_batch_size_count[5m])` |
| Completed public calls/s, including pre-admission rejections | `rate(requestbatcher_execute_duration_milliseconds_count[5m])` |
| Mean items per submission or Handler batch | `rate(<size>_sum[5m]) / rate(<size>_count[5m])` |

Use `histogram_quantile(0.95, sum by (le, requestbatcher_name, requestbatcher_request_type) (rate(<size>_bucket[5m])))`
for P95 size; substitute `requestbatcher_submission_size` or `requestbatcher_batch_size` for `<size>`. For mean duration,
divide the histogram `sum` field by its `count` field or use the corresponding Prometheus `_sum` and `_count` rates.

Existing Counter queries can migrate from `requestbatcher_requests_submitted_total` to
`requestbatcher_submission_size_sum`, from `requestbatcher_requests_processed_total` to
`requestbatcher_batch_size_sum`, and from `requestbatcher_batches_processed_total` to
`requestbatcher_batch_size_count` (or `requestbatcher_handle_duration_milliseconds_count`). Rename older batch-duration
queries from `requestbatcher_batch_duration_milliseconds_*` to `requestbatcher_handle_duration_milliseconds_*`.

## Size Histogram Buckets

The advice uses the largest `BatchSize` B in the service collection, regardless of how many batchers are registered.
`batch.size` uses boundaries `0`, `1`, and `ceil(B * i / 22)` for `i = 1..22`, sorted and deduplicated; for B <= 22,
every integer size is represented. `submission.size` independently uses `0`, `1`, and `ceil(B * i / 17)` for `i = 1..17`,
then adds `2B`, `4B`, `8B`, `16B`, and `32B`; for B <= 17, every integer size is represented. For B = 40 and B = 120,
both use B = 120: `batch.size` uses `ceil(120 * i / 22)` and `submission.size` uses `ceil(120 * i / 17)`, each with
`0` and `1`; the latter also adds `240, 480, 960, 1920, 3840`.

Each size histogram has at most 25 classic Prometheus bucket series including `+Inf`, or 27 series per label set including
`_sum` and `_count`. This upper bound does not grow with the number of batchers; additional label sets still increase total
series. `submission.size` values above `32B` fall into `+Inf`; `batch.size` values above B fall into `+Inf`. All values
still contribute to sum, count, and mean; quantiles are estimated from buckets, so estimates are coarse and the
`submission.size` tail above `32B` cannot be resolved.

Only size histograms carry this advice. `handle.duration` and `execute.duration` retain the OpenTelemetry SDK default
boundaries: `0, 5, 10, 25, 50, 75, 100, 250, 500, 750, 1000, 2500, 5000, 7500, 10000 ms`, plus overflow. Use the same
boundaries across instances when aggregating buckets for quantiles. Direct `AddMeter("RequestBatcher")` uses the advice;
custom `AddView` settings override it. Advice is a recommendation: other consumers may ignore it. A native `MeterListener`
can inspect `Histogram<long>.Advice.HistogramBucketBoundaries`, but receives raw measurements and performs its own aggregation.

## MeterListener

With a custom `System.Diagnostics.Metrics.MeterListener`, explicitly subscribe to each instrument:

```csharp
listener.InstrumentPublished = (instrument, meterListener) =>
{
    if (instrument.Meter.Name == "RequestBatcher")
    {
        meterListener.EnableMeasurementEvents(instrument);
    }
};
```

- Discovery and measurement callbacks alone do not subscribe. Call `EnableMeasurementEvents` once per desired instrument
  per listener; filter `instrument.Name` if needed.
- Register `long` callbacks for both size histograms and `double` callbacks for both duration histograms. Call `Start()`
  before submitting work and keep the listener alive until the batcher has shut down. Earlier measurements are not replayed.
- The backlog gauge also uses a `long` callback. After enabling it, call `RecordObservableInstruments()` to collect
  a current value; `Start()` alone does not periodically sample observable instruments.
- Callbacks run synchronously and may run concurrently. Keep them lightweight and thread-safe, avoid blocking I/O, and
  prevent exceptions from escaping. Copy any tags needed after the callback returns. Listeners provide their own
  aggregation and export.
