# Batch size, concurrency, deduplication, and Task.WhenAll

## Contract and comparison

This benchmark compares in-process request/response batching under a burst of 16,384
logical reads. It changes only benchmark code, not the library API or behavior.
The input count is twice the largest `BatchSize × MaxConcurrency` window, so
even the largest configuration has queued work beyond its first full window.
All paths return the same deterministic value for every input, including duplicate
keys. Deduplication is local to each actual handler batch; there is no cross-batch
cache or promise that equal keys share an invocation.

The end-to-end matrix uses:

- `BatchSize`: 32 or 256.
- `MaxConcurrency`: 4 or 32. The full cross-product separates batch size from
  concurrency, including small/high and large/low configurations.
- `UniqueKeyCount`: 32 (hot set) or 16,384 (no duplicates). A seeded shuffle of a
  balanced key population is reused across paths; duplicates are not artificially
  grouped together.
- `SubmissionSize`: 1 (single requests joined with `Task.WhenAll`), 32 (small
  explicit submissions joined with `Task.WhenAll`), or 16,384 (one explicit
  submission, with no caller-side `Task.WhenAll`). Explicit submissions may be
  split into multiple handler batches.
- Handler strategy: one bulk read of the distinct keys; distinct keys split into
  chunks of up to 32 and read with `Task.WhenAll`; or one read per distinct key
  joined with `Task.WhenAll` inside the handler. Chunking happens after deduplication,
  so it retains the larger handler batch's deduplication opportunities.

Every configuration uses one queue partition, a queue capacity of 16,384, and a
shared downstream semaphore of 32 slots. Each downstream call asynchronously
waits for 1 ms while holding one slot, then returns deterministic values. A bulk
read pays this simulated round-trip once for all its keys; a single-key read pays
it once per key. This intentionally models a bulk-capable API. It does not model
SQL execution, network bandwidth, connection acquisition, or per-key database
cost. Timer resolution and thread-pool scheduling affect actual latency.

`MaxConcurrency` bounds handler batches, not the child operations created by
`Task.WhenAll`. The shared downstream limit prevents the fan-out path from winning
merely by using an unbounded connection pool. The fan-out path still allocates
waiting tasks, which are part of its end-to-end cost.

## Flow and validation

Setup builds and warms the service provider, generates requests and submission
groups, and resolves the pipeline outside measured iterations. Each iteration
submits the same burst, deduplicates each actual batch, queries the simulated
store, and fans responses out to every original request. There is no artificial
start gate or wait to fill batches: measured batching reflects submission and
consumer scheduling, so effective occupancy and deduplication can vary.

Iteration setup resets counters outside measurement. Iteration cleanup checks
all responses, exactly-once handler processing of each original request, total
input count, handler and downstream concurrency limits, and consistent query/key
counts. Missing results and incorrect counters fail the run. Cleanup prints
per-wave handler batches, effective batch size, distinct keys read, deduplication
ratio, downstream calls, and peak handler/downstream concurrency. Counters are
included in both measured paths; diagnostic output and validation are not.

The separate `TaskWhenAllOverheadBenchmarks` isolates task aggregation with 32,
256, and 4,096 tasks. Both controls start all work before waiting; awaiting each
already-started task is not serial execution. Completed-task cases reuse prepared
tasks. Pending-task cases include identical completion-source allocation and
completion work, with asynchronous continuations. Sources complete on one thread
in input order, rather than according to independent network response times, so
their entire elapsed time must not be attributed to `Task.WhenAll`. Generic `Task.WhenAll` also allocates a
results array. These controls measure successful completion only: awaiting each
task can return on the first failure, whereas `Task.WhenAll` waits for all tasks,
so the control is not a drop-in replacement for production failure handling.
All four methods verify an identical checksum during setup.

## Run

```bash
dotnet run --project tests/RequestBatcher.Benchmarks/RequestBatcher.Benchmarks.csproj \
  --configuration Release -- --filter '*DeduplicationConcurrencyBenchmarks*'

dotnet run --project tests/RequestBatcher.Benchmarks/RequestBatcher.Benchmarks.csproj \
  --configuration Release -- --filter '*TaskWhenAllOverheadBenchmarks*'
```

The end-to-end suite has 72 cases. Its `OperationsPerInvoke` is 16,384: reported
mean time and allocation are per logical request, including the cost of the
entire burst. The reciprocal of mean seconds per request is achieved burst
throughput. This is not individual request latency or a sustained arrival-rate
SLO. Microbenchmark results are per aggregation of `TaskCount` tasks.

Use end-to-end mean time and allocated bytes together with the printed downstream
work counts. A bigger batch can reduce duplicate reads; that gain must not be
called lower `Task.WhenAll` overhead. Compare completed and pending microbenchmark
controls to study aggregation costs, then check whether those costs matter beside
actual downstream work. Conclusions about steady traffic, tail latency, or a
specific database require a separate workload with its real arrival pattern and
query cost.

## Measured results — 2026-10-19

All 72 end-to-end cases and 12 aggregation cases completed on macOS Arm64,
.NET 8.0.25, using SDK 10.0.100 and BenchmarkDotNet 0.15.8. The end-to-end job
used one launch, two warmups and eight measured iterations; aggregation used one
launch, three warmups and eight measured iterations. Benchmarks ran sequentially,
without overlapping test or build workloads. The runner could not raise process
priority. These are local measurements, not a dedicated-host capacity result.

Raw reports retain error and standard-deviation columns:

- [End-to-end timing and allocation](end-to-end-results.md)
- [Actual batch occupancy, distinct reads, calls, and concurrency](workload-diagnostics.md)
- [Task aggregation timing and allocation](task-when-all-results.md)

### Hot-key workload

For one explicit submission of 16,384 requests over 32 distinct keys:

| Handler strategy | BatchSize | MaxConcurrency | Mean burst completion | Allocated / request | Downstream calls |
| --- | ---: | ---: | ---: | ---: | ---: |
| Bulk read | 32 | 32 | 29.14 ms | 315 B | 512 |
| Bulk read | 256 | 4 | 23.85 ms | 220 B | 64 |
| Deduplicate, then chunks of 32 with WhenAll | 256 | 4 | 24.11 ms | 225 B | 64 |
| One read per distinct key with WhenAll | 256 | 4 | 79.03 ms | 305 B | 2,047 |
| One read per distinct key with WhenAll | 32 | 32 | 395.46 ms | 755 B | 10,408 |
| Bulk read | 256 | 32 | 6.40 ms | 220 B | 64 |

Burst times are the reported normalized means multiplied by 16,384; they are not
per-request response latency. Increasing batch size from 32 to 256 reduced the
number of distinct-key reads from 10,408 to 2,047, and improved batch-local
deduplication from 36.47% to 87.51%. This explains much of the fan-out improvement:
it is less downstream work, not a faster aggregation primitive.

The large-batch/low-concurrency bulk configuration was about 18% faster and used
about 30% fewer allocated bytes per original request than small-batch/high-
concurrency bulk processing. Large batches with high concurrency were fastest
in this constant-round-trip model, but used 32 active downstream calls rather
than four. This does not establish that 32 concurrent database queries are safe
or beneficial on a real server.

With only 32 possible keys, deduplication leaves at most one chunk of 32. The
chunked strategy therefore creates no additional downstream parallelism in this
hot-set case. Its timing difference from the bulk strategy is within the observed
variation; the extra chunk/aggregation allocations remain visible. The benchmark
deliberately retains the one-chunk WhenAll path rather than optimizing it away.

### No-duplicate workload and caller submission

With 16,384 distinct keys, one explicit submission, and `BatchSize=256` /
`MaxConcurrency=4`, bulk reads averaged 23.72 ms per burst; chunked WhenAll
averaged 23.29 ms. Their uncertainty overlaps, so this is not evidence of a
reliable speedup. Chunking increased downstream calls from 64 to 512 and peak
downstream concurrency from four to 32. With handler concurrency already at 32,
chunking averaged 21.68 ms versus 6.57 ms for bulk reads. Extra fan-out did not
help once the bulk path could use the downstream concurrency limit itself.

Caller-side aggregation also changes library admission and completion costs.
For the hot-set bulk path at `256/4`, submission sizes 1, 32, and 16,384 allocated
327 B, 243 B, and 220 B per original request respectively. Mean burst times were
26.16 ms, 21.44 ms, and 23.85 ms. The single largest submission was not the fastest
on this run, despite allocating least. Those differences must not be called pure
WhenAll overhead: submission APIs, queue admission, actual batch occupancy, and
result aggregation all participate.

### Aggregation overhead in isolation

These are successful, already-completed generic tasks, including checksum
calculation and the outer async method's result task:

| Task count | WhenAll mean | Await already-started tasks mean | WhenAll allocation | Await-each allocation |
| --- | ---: | ---: | ---: | ---: |
| 32 | 0.421 µs | 0.097 µs | 592 B | 72 B |
| 256 | 2.944 µs | 0.673 µs | 3,280 B | 72 B |
| 4,096 | 49.128 µs | 11.675 µs | 49,360 B | 72 B |

The pending-source harness also showed additional allocation and scheduling
cost: at 256 tasks, WhenAll averaged 125.65 µs and 40,377 B, versus 17.34 µs and
28,991 B for await-each. This includes source creation and in-order completion
with asynchronous continuations. It is not a measurement of the WhenAll call
alone, nor a prediction for randomly completing network operations.

For this workload, larger actual handler batches improve deduplication. Use
handler concurrency to expose enough downstream parallelism, and keep a separate
shared limit if handlers fan out. If the downstream accepts one efficient bulk
query, splitting it solely to introduce WhenAll adds work and allocation. When
query-size limits require splitting, deduplicate before splitting to preserve
the larger batch's deduplication scope. Real query cost and arrival patterns must
determine the final batch size and concurrency.

## Observability

The recorded comparison does not include the optional `RequestBatcher.OpenTelemetry` instrumentation or its tracing
overhead. See the [observability design](../../observability-design.md) for span correlation in this workload.
