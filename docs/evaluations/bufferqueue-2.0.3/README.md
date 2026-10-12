# Scheduling evaluation with BufferQueue 2.0.3

The [external I/O follow-up](../external-io-scheduling/README.md) evaluates issue #5 using
real HTTP calls, mixed CPU/I/O handlers, connection limits, and controlled batch formation.

Evaluated 2026-10-08 against RequestBatcher `7326fb31dc763cfa4ce8420af845bcbc6b2c441f` with the dependency changed to BufferQueue 2.0.3. This is an experiment report, not a production scheduling design.

## Evaluation-time Recommendation

The recommendations below record the 2026-10-08 assessment. The current runtime
policy is now defined in the [dispatch design](../../batch-dispatch-redesign.md):
unkeyed batchers use one partition, while keyed batchers use an explicit count.
The measurements remain useful comparisons for choosing that count.

- Keep the current direct-dispatch default. Bounded offloading is worth a separate design for CPU or blocking handlers; the result does not justify scheduling every handler through the ThreadPool.
- Keep one partition when no key is configured. Keyed workloads should choose an explicit count because the measurements show that partition count affects both locality and batch occupancy.
- Partition count remains a RequestBatcher option bound to `UsePartitionKey`; BufferQueue topics, producers, and consumers remain internal.

## Upgrade validation

BufferQueue 2.0.3 supplies fair ring scans and complete-string XXH3 routing. RequestBatcher still owns one pull loop and its handler execution limit; neither upstream fix moves synchronous handler code off that loop. String assignments can change across the upgrade, but RequestBatcher has no persistent pending queue to migrate and promises no handler ordering. System.IO.Hashing 10.0.9 is a new transitive dependency.

Six deterministic integration cases cover common-prefix distribution and equal-key affinity through RequestBatcher's partition-key adapter, each with single-item and batch production, plus alternating sparse-partition reads with auto/manual commit. All 93 unit tests passed. Release solution build, formatting, and package creation passed. The Docker daemon was unavailable, so the database sample and PostgreSQL/Redis benchmarks were not run.

## Method

Environment: macOS 27.0.1, Arm64, 12 process-visible logical processors, SDK 10.0.100, .NET 8.0.25, Release. Tiered compilation was disabled equally for every variant to reduce short-run tiering drift. ThreadPool minimums were unchanged. Results use three measurements after a full-workload warmup, one fresh process per configuration, in a fixed shuffled order. There were 40 configurations and 12 deterministic burst cases; every measured request was checked for exactly one handler visit.

The [evaluation script](../../../scripts/scheduling-evaluation/run.py) copies the current library into a temporary directory and patches only that copy with a partition override and an offload switch. Each offloaded batch acquires its execution permit before entering the ThreadPool. The [method and metric definitions](../../../scripts/scheduling-evaluation/README.md) describe the producer windows, workload sizes, instrumentation, and limitations. Full per-iteration results and batch histograms are in [measurements.jsonl](measurements.jsonl); gate-preloaded results are in [bursts.jsonl](bursts.jsonl).

## Issue #5: handler dispatch

The following uses batch limit 1, eight execution slots, eight partitions, eight producers, and no key. Rates are median requests/second, peaks are the maximum observed handler concurrency across the three measurements. The CPU handler performs 100,000 dependent integer operations per batch; blocking and async handlers use Thread.Sleep(1) and Task.Delay(1), respectively.

| Handler | Direct req/s | Offloaded req/s | Ratio | Actual peak: direct / offload |
| --- | ---: | ---: | ---: | ---: |
| fast | 506,273 | 464,585 | 0.92x | 1 / 5 |
| io | 6,814 | 6,684 | 0.98x | 8 / 8 |
| cpu | 10,168 | 69,911 | 6.88x | 1 / 8 |
| blocking | 817 | 6,968 | 8.53x | 1 / 8 |

CPU occupancy increases from 12.2% to 92.0% and blocking occupancy from 12.1% to 98.8%. These are handler wall-time occupancy figures, not CPU utilization. Async waiting already occupies about 98% of the execution window; the extra scheduling hop offers no benefit in that case.

For fast synchronous handlers, offloading adds roughly 125 bytes/request in the eight-slot case (683 to 808 B/request) and reduces throughput by about 8%. With one execution slot, throughput falls from 498,755 to 192,812 requests/second. These allocation measurements include the entire instrumented pipeline, not only Task.Run. With batch limit 64, CPU throughput improves from 427,526 to 1,104,554 requests/second; actual batch formation also changes, so this is a pipeline comparison rather than an isolated scheduler microbenchmark.

At 64 configured slots, the blocking offload probe reaches only 13 simultaneous handlers. A semaphore bounds scheduled plus running work but does not guarantee that the process ThreadPool supplies 64 runnable workers. Offloading is not an unbounded remedy for blocking I/O.

A production offload design must first resolve the new scheduled-but-not-started interval: the probe schedules the entire ProcessBatchAsync method, so TryStartProcessing moves later. Caller cancellation and infrastructure failure may still terminate a request in that interval. Define the dispatch boundary explicitly, preserve handler/scoped-service lifetime, and guarantee completion plus permit release on scheduling, handler, and shutdown failure paths. Do not use a scheduling cancellation token that can prevent the cleanup delegate from running. These semantics were not validated by this performance-only prototype.

## Issue #6: partitions

The following comparisons hold batch limit 64 and execution limit 8 constant, use direct dispatch, and vary only partition count. String keys use 256 repeated user: keys, shuffled with a fixed seed; skew sends about 90% of work to one key.

| Handler / keys / producers | P=1 req/s | P=8 req/s | P=8 / P=1 |
| --- | ---: | ---: | ---: |
| fast / none / 1 | 2,048,456 | 2,058,851 | 1.01x |
| fast / none / 8 | 1,019,639 | 985,432 | 0.97x |
| fast / uniform / 8 | 1,023,358 | 1,201,974 | 1.17x |
| fast / skew / 8 | 1,052,334 | 1,017,225 | 0.97x |
| io / none / 8 | 372,028 | 375,974 | 1.01x |
| io / uniform / 8 | 375,752 | 366,124 | 0.97x |
| io / skew / 8 | 372,578 | 316,705 | 0.85x |

The partition result depends on workload. For example, uniform-key fast handling benefits from eight partitions here, while skewed async handling falls about 15%, with mean batch size dropping from 63.4 to 50.8. Fairness between readable partitions does not maximize batch occupancy or downstream efficiency. Small differences such as 1–4% are not grounds for selecting a new default from a single process launch.

Gate-preloaded explicit submissions separate batch fragmentation from producer scheduling:

| Workload | P=1 batches / distinct-key work | P=8 batches / distinct-key work |
| --- | ---: | ---: |
| 64 requests, no routing key | 1 / 64 | 8 / 64 |
| 2,048 requests, uniform string keys | 32 / 1,866 | 36 / 971 |
| 2,048 requests, skewed string keys | 32 / 233 | 37 / 172 |

Distinct-key work sums the distinct keys in each handler batch. It represents opportunities for per-batch merging, not measured database calls or latency. Uniform-key partitioning reduces this count by about 48%, but increases invocation count from 32 to 36 because partitions have uneven tails. For short bursts, one partition can avoid multiple partially filled calls. The best choice depends on the downstream fixed cost per invocation versus cost per distinct item. Equal-key requests can still span batches and execute concurrently; cross-batch version checks in the deduplication sample remain necessary.

## Remaining evidence and next steps

- The workload is bounded by per-producer windows, not an open-loop arrival stream. It does not establish a saturation QPS threshold or production p99. PreHandlerP99Ms includes admission and scheduling, not just queue residence.
- Only selected combinations were tested. Partition comparisons use H=8; H=64 tests fix P=8 to isolate scheduling, rather than using the production core-count cap of 12 on this host.
- No direct old-package/new-package performance comparison was performed. These measurements reevaluate scheduling choices on 2.0.3; they do not measure the speedup of upgrading from 2.0.2.
- Before changing dispatch defaults, use longer repeated benchmark launches, representative handler costs, ThreadPool queued-work traces, and deterministic lifecycle tests for any offload implementation.
- Before changing partition defaults, add paced short bursts and downstream fixed/per-key cost models, plus precise admission/queue/dispatch timing. Run PostgreSQL/Redis end-to-end checks when Docker is available.
- Use the measured P=1 and P=8 comparisons as workload guidance. The best count depends on key distribution, burst size, and downstream fixed cost; equal-key requests can still span batches.

## Reproduce

```bash
python3 scripts/scheduling-evaluation/run.py --output artifacts/scheduling-evaluation
```

The script uses the SDK from global.json and normal NuGet configuration. If the user package cache is not writable, set NUGET_PACKAGES and NUGET_HTTP_CACHE_PATH to writable temporary directories. The generated variants never modify production source in the checkout.
