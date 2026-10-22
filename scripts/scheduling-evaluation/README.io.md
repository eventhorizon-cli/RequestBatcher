# External I/O scheduling evaluation

Run from the repository root:

```bash
dotnet run --file scripts/scheduling-evaluation/EvaluateHttpScheduling.cs -- --output artifacts/http-scheduling-evaluation
```

For a small build and loopback smoke test (one unkeyed direct/offload pair), use:

```bash
dotnet run --file scripts/scheduling-evaluation/EvaluateHttpScheduling.cs -- --smoke --output artifacts/http-scheduling-smoke
```

This reuses the isolated source preparation in `EvaluateScheduling.cs` and replaces only the temporary
probe. It does not change the checkout's production code. The experimental offload
switch schedules the `_batchConsumer.ExecuteAsync` invocation through `Task.Run`
after acquiring an execution slot, then uses the existing
`ProcessBatchAsync(ValueTask)` cleanup path. As in the original experiment, this is
a performance probe with an unresolved cancellation boundary, not a production
implementation. The HTTP probe omits `UsePartitionKey`, so it exercises the current
public unkeyed one-partition policy rather than a hidden partition override.

`IoServer.cs` runs Kestrel in a separate process, bound to a temporary loopback port.
Each HTTP/1.1 request waits asynchronously for its configured service delay and echoes
the number of logical items handled. `IoProgram.cs` uses a reused `HttpClient`, a warmed
connection pool, and the real RequestBatcher coordinator and BufferQueue 2.0.3. It sends
one HTTP request per handler batch and validates every response, each item's single
handler visit, and the server's total call and item counts. No database is involved.
The runner terminates the HTTP server on completion or failure.

The 12 scenarios compare:

- Asynchronous HTTP with 1, 10, and 50 ms service delay at eight execution slots.
- Asynchronous HTTP at 32 slots with either 32 or eight connections.
- 250,000 or 2,000,000 fixed integer operations before asynchronous HTTP.
- 2,000,000 identical operations after asynchronous HTTP, at eight and 32 slots.
- Synchronous `HttpClient.Send` at eight and 32 slots as a blocking control.

All use one unkeyed partition, a batch limit of 32, a queue capacity of 8,192, and a
producer window of 1,024 outstanding logical requests. Each configuration gets three
fresh client processes; each process warms 4,096 logical requests and measures three
iterations of 8,192. The 72 processes run in a fixed shuffled order. The server is reused
across these sequential clients. Provider creation, connection warmup, GC before each
iteration, server counters, and shutdown are outside measurement. Tiered compilation is
disabled equally for the server and both client variants. ThreadPool minimums are unchanged.

The CPU loop has a fixed operation count, not a busy wait for a fixed wall time;
`CpuWorkMsPerBatch` reports its observed cost, including any preemption. This keeps the
amount of computation equal even when scheduling and CPU contention differ.

Measurements include logical requests/second, per-item submission-to-completion p50/p99,
submission-to-handler-start p99, average batch size, peak active handlers, actual server
concurrency, whole-client allocations and CPU time, and ThreadPool thread/pending-work
counts sampled approximately every 10 ms. The handler's HTTP duration includes the actual
single external I/O elapsed time: client
connection-pool waiting, network transfer, and response-body read. Server-observed request time
and the handler's total time are separate measurements; server counts do not include client-side
connection-pool waits. `HttpCall`, `ServerResidence`, and `HandlerDuration` report those three
durations respectively. Sampling can miss short spikes and can itself be delayed by starvation.
Pending work includes the entire client process, not just batches.

The individual p50/p99 values come from the finite measured sample for each configuration, so
they are estimates of this run rather than production latency guarantees. In a latency sweep,
explicit batch submission completes a whole submitted group together; its end-to-end timestamps
therefore represent whole-group completion and must not be interpreted as the latency of one
individual request.

Summaries should first take the median of each process's configured measured iterations,
then the median of the configured process medians. Preserve the launch range to distinguish
small noisy differences from repeatable gains. This is a bounded-window comparison,
not an open-loop saturation test; its p99 is not a production latency estimate. Loopback
HTTP omits TLS, remote network variability, real database pools/locks, large payloads,
retries, cache hits and rate limits. The two processes share the host's CPU resources.

Generated files stay in the local output directory and should not be committed. Keep
only concise result summaries in `docs/evaluations`. The generated `environment.json`
records SDK, source commit, dirty library diff and
experimental parameters. `measurements.jsonl` contains all measurements; `server.log`
contains server failures, if any. The original experiment's README documents the
in-flight/cancellation limitations of the temporary offload patch.

To control batch formation, run the five pure asynchronous HTTP cases with each window
submitted through the explicit batch API:

```bash
dotnet run --file scripts/scheduling-evaluation/EvaluateHttpScheduling.cs -- --batch-control --output artifacts/http-batch-control
```

This runs 30 additional client processes by default. The explicit submission produces
full batches in the recorded experiment; this is not a public guarantee. Its completion
timestamps record when the **whole submission** completes, so use this control for
throughput/batch-formation comparisons and do not compare its p99 directly with the
individual-submission latency. `--launches` changes the number of fresh processes per
configuration in either experiment.

To measure the effect of actual external I/O latency, run the focused sweep:

```bash
dotnet run --file scripts/scheduling-evaluation/EvaluateHttpScheduling.cs -- --latency-sweep \
  --delays 10 100 500 --output artifacts/http-latency-sweep
```

The sweep forces explicit batch submission and compares both offload modes for each delay
using asynchronous HTTP at 8/8, 32/32, and 32/8 handler/connection concurrency, plus
2,000,000 prefix CPU operations at 32/32. It defaults to 4,096 warmup requests, 8,192
measured requests, and three measured iterations. Use `--measured-requests`,
`--warmup-requests`, and `--iterations` to change those counts; request counts must be
positive multiples of 1,024. `environment.json` records the sweep flag, effective delay
list, and the actual counts and iteration count used.

## Observability

The HTTP probe's timing hooks are separate from the built-in RequestBatcher instrumentation; reported
historical results do not claim to include those spans. See the [observability design](../../docs/observability-design.md)
for the `Execute` call span and linked `HandleBatch` Consumer span.
