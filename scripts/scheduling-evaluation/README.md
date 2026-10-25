# Scheduling evaluation

For real HTTP I/O, synchronous preparation, connection-pool limits, and a batch-formation
control, see the [external I/O experiment](README.io.md).
For current-source paced arrivals, slot utilization, caller tail latency, and
PostgreSQL/Redis comparisons, see the [arrival-rate probe](README.arrival.md).

Run from the repository root with the SDK selected by `global.json`:

The script and every temporary evaluation project it generates target .NET 10 and C# 12.

```bash
dotnet run --file scripts/scheduling-evaluation/EvaluateScheduling.cs -- --output artifacts/scheduling-evaluation
```

The script builds an isolated copy of the current library in a temporary directory.
It adds one experimental switch only to that copy: after the execution permit and
in-flight registration, the offload variant schedules the call to
`_batchConsumer.ExecuteAsync` and keeps the existing `ProcessBatchAsync(ValueTask)`
cleanup path. The checkout's runtime and public options are unchanged. The offload
variant is a performance probe, not a proposed implementation: moving
`TryStartProcessing` into the scheduled delegate changes the cancellation window,
and the prototype has explicit cleanup for a synchronous invocation failure.
A production design needs to resolve that boundary and scheduling-failure cleanup.

Run the deterministic scheduling self-check without the matrix:

```bash
dotnet run --file scripts/scheduling-evaluation/EvaluateScheduling.cs -- --self-check --output artifacts/scheduling-self-check
```

The check gates the scheduled delegate until after submission returns and fails if
the handler body is entered inline.

Partition topology is configured through the public API in every probe. Unkeyed cases
omit `UsePartitionKey` and therefore use the production one-partition policy. Cases
with more than one partition use `UsePartitionKey(work => work.Key, partitionCount)`;
the probe does not force a hidden multi-partition topology for unkeyed work. The
recorded matrix contains 33 regular configurations and six preloaded burst cases.

The script runs 33 configurations in a fixed shuffled order, one fresh process per
configuration, with one full workload warmup and three measured workloads. Tiered
compilation is disabled equally for both variants to reduce short-run JIT drift.
The .NET ThreadPool uses its defaults; the script does not raise minimum threads.
It writes runtime details, the library diff, per-run measurements, and deterministic
preloaded-burst results to the local output directory. Keep generated artifacts outside
version control; committed evaluation records contain only concise README summaries. NuGet cache paths can be supplied
through the usual `NUGET_PACKAGES` and `NUGET_HTTP_CACHE_PATH` environment variables.

The workloads use the real coordinator, dependency injection, and BufferQueue:

- Immediate synchronous completion, asynchronous `Task.Delay(1)`, fixed synchronous
  integer work, and synchronous `Thread.Sleep(1)`, with batch limits 1 and 64.
- Execution limits 1, 8, and 64; unkeyed cases use one partition, while keyed
  routing cases use eight partitions and eight producers.
- Unkeyed traffic, uniformly repeated full string keys, and 90% hot-key traffic.
  Inputs are shuffled with a fixed seed. This is a selected matrix, not the
  complete Cartesian product of every parameter.
- Each producer submits at most 1,024 requests before awaiting that window. Queue
  capacity is 8,192. All work must complete exactly once. These are bounded-load
  diagnostic workloads, not open-loop arrival-rate or production capacity tests.
- Six burst cases first occupy every handler slot with gates, preload 64 or 2,048
  items through explicit batch submission, and then release the gates. They record
  handler batch sizes and total distinct-key work after per-batch deduplication.

Provider creation, request allocation, pre-measurement GC, and shutdown are outside
the measured interval. Time includes producer scheduling, admission, handling, and
completion. `PreHandlerP99Ms` includes admission/capacity wait, queue residence, and
scheduling delay; it is not isolated queue wait or caller-observed end-to-end p99.
`Occupancy` sums handler wall time divided by elapsed time and the execution limit;
it counts asynchronous waiting, not CPU utilization. `Peak` measures actual handler
concurrency, not all reserved execution slots. Allocations and GC counts cover the
whole process during measurement, including instrumentation and submission tasks.
The batch histogram also includes instrumentation cost. No ThreadPool queue-length
trace or precise enqueue/dispatch split is collected.

These finite-window diagnostics alone do not justify changing defaults. The
[arrival-rate evaluation](../../docs/evaluations/arrival-scheduling/README.md)
adds paced and downstream comparisons; neither prototype validates production
lifecycle semantics. PostgreSQL/Redis workloads require Docker.

## Observability

These scheduling probes use their own diagnostic counters and are not measurements of the optional
`RequestBatcher` spans. Applications can register those spans with
`AddRequestBatcherInstrumentation()`; see the [observability design](../../docs/observability-design.md).
