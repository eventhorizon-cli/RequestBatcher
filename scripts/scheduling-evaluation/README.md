# Scheduling evaluation

For real HTTP I/O, synchronous preparation, connection-pool limits, and a batch-formation
control, see the [external I/O experiment](README.io.md).

Run from the repository root with the SDK selected by `global.json`:

```bash
python3 scripts/scheduling-evaluation/run.py --output artifacts/scheduling-evaluation
```

The script builds an isolated copy of the current library in a temporary directory.
It adds two experimental switches only to that copy: a fixed partition count and
`Task.Run` around `ProcessBatchAsync`, after the existing execution permit and
in-flight registration. The checkout's runtime and public options are unchanged.
The offload variant is a performance probe, not a proposed implementation: moving
`TryStartProcessing` into the scheduled delegate changes the cancellation window.
A production design needs to resolve that boundary and scheduling-failure cleanup.

The script runs 40 configurations in a fixed shuffled order, one fresh process per
configuration, with one full workload warmup and three measured workloads. Tiered
compilation is disabled equally for both variants to reduce short-run JIT drift.
The .NET ThreadPool uses its defaults; the script does not raise minimum threads.
It writes runtime details, the library diff, per-run measurements, and deterministic
preloaded-burst results to the output directory. NuGet cache paths can be supplied
through the usual `NUGET_PACKAGES` and `NUGET_HTTP_CACHE_PATH` environment variables.

The workloads use the real coordinator, dependency injection, and BufferQueue:

- Immediate synchronous completion, asynchronous `Task.Delay(1)`, fixed synchronous
  integer work, and synchronous `Thread.Sleep(1)`, with batch limits 1 and 64.
- Execution limits 1, 8, and 64; partition counts 1 and 8; one or eight producers.
- No key, uniformly repeated full string keys sharing `user:`, and 90% hot-key
  traffic. Inputs are shuffled with a fixed seed. This is a selected matrix, not
  the complete Cartesian product of every parameter.
- Each producer submits at most 1,024 requests before awaiting that window. Queue
  capacity is 8,192. All work must complete exactly once. These are bounded-load
  diagnostic workloads, not open-loop arrival-rate or production capacity tests.
- Burst cases first occupy every handler slot with gates, preload 64 or 2,048
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

A full BenchmarkDotNet campaign, additional launches, paced latency measurements,
and downstream database validation remain necessary before changing defaults.
The PostgreSQL/Redis benchmarks are separate opt-in workloads requiring Docker.
