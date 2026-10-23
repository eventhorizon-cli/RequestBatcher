# Queue pull and dispatch capacity

This evaluation tests whether pulling one handler batch at a time limits the current
pipeline with approximately 20 ms asynchronous downstream calls. It changes no production
scheduling behavior. Tracing and metrics listeners are disabled.

For this full-batch, 20 ms asynchronous workload, pulling one batch per reserved slot
does not appear to be the limiting factor. Handler occupancy was 96.2–99.1%, and the
complete pipeline took 0.8–3.0% longer than the ideal direct full-batch control.
Keep the current scheduling contract; these results do not justify adding multi-slot
reservation and multi-batch prefetching.

## Results

Measured on 2026-10-23 against the production source in `3f939c3`, Apple M2 Max,
macOS 27.0.1 Arm64, SDK 10.0.100, .NET 8.0.25, BenchmarkDotNet 0.15.8.
All 20 cases / 40 process launches completed without concurrent local builds or tests.
The end-to-end cases validated 1,568 waves / 6,422,528 requests; queue-only cases
validated 104 drains / 425,984 records. These counts include warmup and diagnostic
invocations. Every ID was observed exactly once. No Docker services were started.

Prefilled queue results (99.9% confidence-interval margin after ±):

| Batch limit | Partitions | Time per record | Derived time for 4,096 records | Allocation per record | Synchronous pulls |
| ---: | ---: | ---: | ---: | ---: | ---: |
| 32 | 1 | 25.45 ± 0.812 ns | 0.104 ms | 10 B | 128/128 |
| 32 | 16 | 25.68 ± 1.437 ns | 0.105 ms | 10 B | 128/128 |
| 128 | 1 | 14.99 ± 1.200 ns | 0.061 ms | 8 B | 32/32 |
| 128 | 16 | 14.76 ± 0.481 ns | 0.060 ms | 8 B | 32/32 |

BatchSize 32 corresponds to approximately 0.81–0.82 microseconds per pull including
record bookkeeping. One and sixteen populated partitions are indistinguishable within
these intervals. Larger batches amortize fixed pull costs. This standalone result must
not be treated as a profile of pull time inside a contended production pipeline.

For 20 ms handlers, times below cover all 4,096 requests, with 128 full handler batches
in every wave. Observed handler duration averaged 20.95–21.13 ms across launches.

| Concurrency | Partitions | Direct mean ± error | RequestBatcher mean ± error | Additional elapsed time | Library handler occupancy |
| ---: | ---: | ---: | ---: | ---: | ---: |
| 8 | 1 | 336.21 ± 1.01 ms | 338.98 ± 1.27 ms | 0.82% | 99.07–99.09% |
| 8 | 16 | 335.69 ± 0.73 ms | 340.42 ± 1.05 ms | 1.41% | 98.83–98.86% |
| 32 | 1 | 84.09 ± 0.50 ms | 86.46 ± 0.82 ms | 2.82% | 96.97–97.17% |
| 32 | 16 | 84.44 ± 0.66 ms | 86.96 ± 1.17 ms | 2.98% | 96.19–96.62% |

Increasing concurrency from 8 to 32 reduces wave time by approximately 3.9 times,
and the actual handler peak reaches the configured limit in every asynchronous case.
The direct control does no queueing or completion accounting, so the elapsed difference
covers much more than pull calls. Even the entire measured pipeline overhead is small
relative to downstream waiting here.

With synchronous handlers, direct waves take 0.150–0.153 ms and library waves take
1.275–1.581 ms. The queue-only drain of about 0.105 ms is approximately 7–8% of that
library wave duration as a scale comparison, not a measured attribution. Submission,
pending state, completion, and dispatch costs become visible when downstream work is
negligible. Increasing the execution limit cannot parallelize a handler that completes
inline on the sole dispatch loop. The synchronous results do not support claiming that
queue pulling is never a bottleneck under other workloads.

Raw data: [queue CSV](queue-pull.csv), [end-to-end CSV](dispatch-capacity.csv), and
[validation records](validation.txt). Full BDN logs are at
`/private/tmp/requestbatcher-queue-pull-capacity-final` and
`/private/tmp/requestbatcher-queue-pull-capacity-final.log`.

BDN warns that queue-only and synchronous iterations are shorter than its recommended
100 ms. These short measurements establish the scale difference, not a precise
sustainable throughput ceiling. Configuration order is fixed, populated partitions
are balanced, high-priority scheduling was unavailable, and the explicit submission
forms full batches. Sparse/hot-key traffic,
concurrent producers, empty-queue wakeups, and actual downstream services are not measured.
There is no before/after multi-batch-pull implementation comparison.

## Method

- `QueuePullBenchmarks` drains 4,096 prefilled BufferQueue records using one consumer,
  batch limits 32/128 and partition counts 1/16. Producer work is outside measurement.
  It checks synchronous pull completion and exactly-once delivery. This is a warm,
  nonempty-queue measurement, not producer contention or empty-queue wakeup latency.
  Records are reference objects containing an integer ID; no RequestBatcher pending state
  is constructed. Timing includes enumerator creation/disposal, per-item ID bookkeeping,
  and per-batch size/synchronous-completion counters. Validation and the 30-second safety
  timeout setup are outside measurement. Integer routing spreads 256 records into each
  of sixteen partitions, and each drain verifies those counts. Segment size follows
  production (`max(16, BatchSize)`). Reported BDN time/allocation is per record because
  the pull benchmark declares 4,096 operations per invocation.
- `DispatchCapacityBenchmarks` submits 4,096 distinct requests in one explicit submission,
  with batch limit 32, execution limits 8/32, and one unkeyed partition or sixteen keyed
  partitions (`request % 256`). Handlers complete synchronously or await `Task.Delay(20)`.
  Each configuration also runs a direct full-batch control using the same handler and
  execution limit but no queue, routing, admission, or request completion bookkeeping.
  The direct control deliberately has ideal batch packing; its sixteen-partition case
  repeats the same direct workload and does not simulate partition routing.
- Both paths validate every request ID exactly once and enforce the handler batch size
  and concurrency limits. Handler counters, timestamp reads, and ID checks add diagnostic
  cost equally to the direct and library handlers. End-to-end timing includes submission
  and final validation; aggregate diagnostic wave time excludes final validation.
- Occupancy is summed handler wall time divided by wave time and concurrency. It includes
  asynchronous I/O waiting and is not CPU utilization. A synchronous handler naturally
  has peak concurrency one because it finishes inline before the next dispatch.

BenchmarkDotNet runs two fresh processes per case, three warmup iterations, and eight
measurement iterations. The end-to-end job performs four waves per iteration. Queue
refill/validation happen in iteration setup/cleanup with one drain per iteration.
These finite bursts measure available capacity for this workload; they do not establish
an open-loop saturation limit, per-request p99, or the speedup of an unimplemented
multi-batch pull API. Direct-versus-library differences include the whole pipeline and
must not be attributed entirely to pulling.

## Reproduce

Run from the repository root with its selected SDK, without concurrent benchmarks:

```bash
dotnet run --project tests/RequestBatcher.Benchmarks/RequestBatcher.Benchmarks.csproj \
  --configuration Release -- \
  --filter '*QueuePullBenchmarks*' '*DispatchCapacityBenchmarks*' \
  --unrollFactor 1 --artifacts /private/tmp/requestbatcher-queue-pull-capacity
```

No containers or external services are required. Source files are
[QueuePullBenchmarks](../../../tests/RequestBatcher.Benchmarks/InMemory/QueuePullBenchmarks.cs)
and [DispatchCapacityBenchmarks](../../../tests/RequestBatcher.Benchmarks/InMemory/DispatchCapacityBenchmarks.cs).
