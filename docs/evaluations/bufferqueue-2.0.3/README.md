# Scheduling evaluation with BufferQueue 2.0.3

Evaluated 2026-10-08 against `7326fb3` with BufferQueue 2.0.3. macOS 27.0.1 / Arm64,
12 visible logical processors, SDK 10.0.100, .NET 8.0.25, Release; tiering disabled
and ThreadPool minimums unchanged. Three measurements follow a full-workload warmup
in one fresh process per configuration, with fixed shuffled order and exactly-once validation.

**Keep direct dispatch for asynchronous I/O and one partition when no key is needed.**
Keyed workloads should select a partition count using batch occupancy and key distribution.
The [external I/O follow-up](../external-io-scheduling/README.md) tests real HTTP calls.

## Issue #5: handler dispatch

Batch limit 1, eight execution slots, eight partitions, eight producers, no key.
Rates are median requests/second; peaks are maximum observed concurrency. CPU work uses
100,000 dependent operations, blocking uses `Thread.Sleep(1)`, and I/O uses `Task.Delay(1)`.

| Handler | Direct req/s | Offloaded req/s | Ratio | Actual peak: direct / offload |
| --- | ---: | ---: | ---: | ---: |
| fast | 506,273 | 464,585 | 0.92x | 1 / 5 |
| io | 6,814 | 6,684 | 0.98x | 8 / 8 |
| cpu | 10,168 | 69,911 | 6.88x | 1 / 8 |
| blocking | 817 | 6,968 | 8.53x | 1 / 8 |

Offloading helps synchronous CPU/blocking handlers in this probe but adds about
125 B/request for fast handlers (683 → 808 B) and reduces their throughput.
It does not increase downstream capacity. The offload prototype's cancellation and
lifecycle semantics were not validated for production.

## Issue #6: partitions

Direct dispatch, batch limit 64 and execution limit 8; only partition count varies.
Uniform keys use 256 repeated strings, shuffled with a fixed seed; skew sends about
90% of requests to one key.

| Handler / keys / producers | P=1 req/s | P=8 req/s | P=8 / P=1 |
| --- | ---: | ---: | ---: |
| fast / none / 1 | 2,048,456 | 2,058,851 | 1.01x |
| fast / none / 8 | 1,019,639 | 985,432 | 0.97x |
| fast / uniform / 8 | 1,023,358 | 1,201,974 | 1.17x |
| fast / skew / 8 | 1,052,334 | 1,017,225 | 0.97x |
| io / none / 8 | 372,028 | 375,974 | 1.01x |
| io / uniform / 8 | 375,752 | 366,124 | 0.97x |
| io / skew / 8 | 372,578 | 316,705 | 0.85x |

**Without a routing requirement, eight partitions provide no consistent gain here
(0.97–1.01×).** Small differences from one process launch do not establish a better default.
Skewed asynchronous traffic loses about 15%, with mean batch size falling from 63.4 to 50.8.

Gate-preloaded explicit submissions isolate batch fragmentation:

| Workload | P=1 batches / distinct-key work | P=8 batches / distinct-key work |
| --- | ---: | ---: |
| 64 requests, no routing key | 1 / 64 | 8 / 64 |
| 2,048 requests, uniform string keys | 32 / 1,866 | 36 / 971 |
| 2,048 requests, skewed string keys | 32 / 233 | 37 / 172 |

Distinct-key work sums distinct keys in each handler batch; it is a deduplication opportunity
count, not measured downstream calls. One partition handles the short unkeyed burst in
one batch; eight require eight partial batches. Equal keys can still span batches and
execute concurrently. The best keyed count depends on downstream fixed and per-item cost.

## Limits and reproduction

These are bounded producer windows, not open-loop saturation or production p99 tests.
Allocation includes the instrumented pipeline; occupancy includes asynchronous waiting.
No old/new BufferQueue package speedup was measured.

```bash
dotnet run --file scripts/scheduling-evaluation/EvaluateScheduling.cs -- --output /private/tmp/scheduling-evaluation
```

The [current probe](../../../scripts/scheduling-evaluation/README.md) uses public routing
configuration. Historical unkeyed multi-partition cases above required an experimental
override; current production deliberately uses one unkeyed partition. The original matrix
had 40 configurations and 12 bursts; the current probe uses 33 configurations and six bursts.

## Observability

The historical BufferQueue comparison does not include the built-in RequestBatcher instrumentation.
See the [observability design](../../observability-design.md) for the current `Execute` and `ProcessBatch` spans.
