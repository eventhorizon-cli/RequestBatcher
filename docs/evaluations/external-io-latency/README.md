# Single-call latency and dispatch scheduling

This 2026-10-10 follow-up measures the duration of each downstream call explicitly.
The [previous evaluation](../external-io-scheduling/README.md) configured server waits
of 1, 10 and 50 ms. Those values were not measured HTTP call durations. Its end-to-end
p99 also included RequestBatcher queueing and submission completion; it must not be
interpreted as the latency of one downstream operation.

## Timing boundaries

The updated probe records one timing sample per handler batch / HTTP call:

- `HttpCall`: immediately before `HttpClient.SendAsync` until the response body is read
  and its item count validated. This includes client connection-pool waiting, transport,
  server work and client continuation scheduling. It excludes RequestBatcher queueing
  and deliberately added synchronous prefix/suffix computation.
- `ServerResidence`: endpoint entry until just before the response is constructed, measured
  on the server and returned in a response header. It includes the configured asynchronous
  delay and server scheduling. It excludes client connection-pool wait, transport and
  response transmission; subtracting it from `HttpCall` does not isolate pool wait alone.
- `HandlerDuration`: handler entry to completion, including synchronous computation and
  the HTTP call. This is active-handler wall time, not the complete execution-slot holding
  time: it excludes waiting for an offloaded delegate to start, and coordinator/consumer
  work before entry and after return. Offload queueing remains in pre-handler and whole-
  submission latency; a short handler time alone does not prove a scheduling improvement.

Each distribution records count, mean, P50, P95 and P99. Raw per-call measurements are
retained so percentiles can be inspected instead of inferred from throughput. Calls per
second and time-averaged active handlers are reported alongside logical requests per
second. The latter should not be confused with downstream call rate: each full handler
batch in this experiment contains 32 logical requests but makes one HTTP call.

## Experiment

The latency sweep uses 10, 100 and 500 ms configured server delay, direct and bounded
ThreadPool dispatch, and the same separate-process loopback HTTP server as before.
For each delay, it covers pure asynchronous HTTP at H/C = 8/8, 32/32 and 32/8, plus a
2,000,000-operation synchronous prefix at H/C = 32/32. H is the handler execution limit;
C is the client's HTTP/1.1 connection limit. Both modes use one unkeyed partition.

The producer submits explicit 1,024-item windows to control batch formation. This is a
finite fill/drain workload, not a continuous arrival-rate test. All batches are checked
for their actual size; full batches observed here are not a public API guarantee.
Two fresh client launches per mode/scenario each warm 1,024 items, then measure three
iterations of 2,048 items. At 32 items per call, this gives 64 calls per iteration and
384 measured calls per mode/scenario. The 48 launches run in a fixed shuffled order.

Provider and connection setup, warmup, pre-iteration GC, counter verification and shutdown
are outside the timed region. The client checks every response, every item's single
handler visit, and server totals. ThreadPool minimums remain unchanged; tiered compilation
is disabled equally. The environment is .NET 8 / SDK 10.0.100 on the same local Arm64 host.

The sample size supports diagnostic comparisons of these deliberately stable delays,
not a production tail-latency guarantee: with 64 calls, the nearest-rank per-iteration
P99 is the maximum sample. Explicit-submission completion latency is the completion of
an entire window and is reported separately from the actual single-call timings.

## Interpreting one call's duration

For one downstream call per batch and a continuously supplied workload, a useful
approximation is:

```text
handler calls/second ≈ time-averaged active handlers / mean handler duration in seconds
logical requests/second ≈ handler calls/second × mean items per handler batch
```

`MaxConcurrency` is an upper limit, not the measured active-handler average. The finite
windows in this experiment drain completely between submissions, and synchronous prefix
work can make filling a window take time; both reduce the average below that upper limit.
Using observed mean duration explains throughput; a P99 duration is for assessing tails,
not the denominator of an average-throughput estimate.

For example, ignoring overhead and assuming enough downstream connections and sustained
full batches, H=8 and BatchSize=32 give these approximate upper-envelope rates:

| Mean handler duration | Handler / downstream calls per second | Logical requests per second |
| --- | ---: | ---: |
| 10 ms | 800 | 25,600 |
| 100 ms | 80 | 2,560 |
| 500 ms | 16 | 512 |
| 1 second | 8 | 256 |

These are arithmetic illustrations, not measured results or recommended capacities.
A slower call holds the execution slot longer; adding a ThreadPool hop does not shorten
its external wait. Raising H helps only while downstream capacity and CPU permit it.
When C < H, waiting for a connection is already included in measured `HttpCall` and
`HandlerDuration`. Do not divide by that inflated duration and also substitute `min(H,C)`
for active handlers: that double-counts the connection-pool constraint. Alternatively,
a downstream-focused model can use active connections and time per occupied connection,
but that is a different measurement boundary.

If a handler makes multiple sequential calls, their durations add to the handler's slot
occupancy. If it fans out calls concurrently, active downstream calls can exceed active
handlers. The one-call-per-batch results here do not model either behavior.

For a simplified sustained workload with a synchronous dispatch-path prefix S and one
asynchronous wait L per handler, direct dispatch has two approximate ceilings:

```text
start-rate ceiling: 1 / S batches per second
execution-window ceiling: H / (S + L) batches per second
```

All times are in seconds. The lower ceiling dominates before considering CPU, connection
or server capacity. At fixed H, increasing L makes the execution window more likely to
be the bottleneck, reducing the value of moving the prefix off the dispatch path. With
S=2 ms and H=32, the ceilings cross near L=(H−1)S=62 ms. This is a simplified explanation,
not a fitted performance prediction. Finite windows repeatedly pay the serial startup
cost; the measured prefix cases can still benefit above that crossover. If H increases
with L to preserve a target call rate, the prefix can become limiting again.

## Measured single-call durations

The timing columns below pool all 384 HTTP calls for each direct-dispatch case.
Throughput takes the median within each launch and then the median of the two launches.
All measured batches contained 32 items. Service time is the observed endpoint residence,
not merely the requested timer duration. All values are milliseconds except calls/second.

| Requested delay | H/C | HTTP mean | HTTP P50 | HTTP P95 | HTTP P99 | Server mean | Calls/second |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 10 | 8/8 | 11.94 | 12.32 | 13.22 | 13.85 | 11.46 | 656.85 |
| 10 | 32/8 | 30.68 | 26.08 | 51.13 | 51.77 | 11.87 | 644.41 |
| 10 | 32/32 | 12.30 | 12.43 | 13.03 | 13.17 | 11.73 | 2475.68 |
| 100 | 8/8 | 103.21 | 103.34 | 104.56 | 105.29 | 101.80 | 76.97 |
| 100 | 32/8 | 259.40 | 210.85 | 417.97 | 418.63 | 102.16 | 76.63 |
| 100 | 32/32 | 105.05 | 104.18 | 111.13 | 112.74 | 101.99 | 300.65 |
| 500 | 8/8 | 503.26 | 503.40 | 504.32 | 504.65 | 501.86 | 15.87 |
| 500 | 32/8 | 1258.93 | 1008.68 | 2016.08 | 2017.36 | 502.08 | 15.87 |
| 500 | 32/32 | 503.75 | 503.93 | 505.17 | 505.89 | 501.75 | 63.23 |

At H/C=8/8, the actual mean HTTP call grows from 11.94 ms to 103.21 ms to 503.26 ms.
Throughput falls with that duration even though the scheduler and batch size are unchanged.
At H/C=32/8 and 500 ms requested delay, the mean client call reaches 1,258.93 ms and P99
reaches 2,017.36 ms: 32 handlers compete for eight connections. The server still handles
about 500 ms of work per call, but later calls in the window wait for earlier waves. Raising
handler concurrency without enlarging downstream capacity does not remove that wait.
The same case at H/C=32/32 does not have this multi-wave connection-pool delay.

All pure asynchronous cases have an offload/direct throughput ratio between 0.97 and 1.02
in this run. Neither mode shortens the external service wait. Small timing differences
include host scheduling and timer variation and should not be generalized as fixed costs.

## Synchronous prefix versus I/O duration

These cases all use a fixed 2,000,000-operation prefix, H/C=32/32, and full 32-item batches.
They directly test how the relative value of offloading changes when only the configured
I/O delay changes. This full-batch control differs from the earlier individual-submission
case that showed 2.18x; its ratios must not be compared as if all inputs were identical.

| Requested delay | Direct calls/s | Offload calls/s | Offload / direct | HTTP mean, direct / offload | Handler mean, direct / offload |
| --- | ---: | ---: | ---: | ---: | ---: |
| 10 ms | 461.56 | 1813.75 | 3.93x | 10.50 / 12.52 ms | 12.32 / 14.72 ms |
| 100 ms | 194.30 | 262.82 | 1.35x | 100.68 / 104.52 ms | 102.66 / 108.43 ms |
| 500 ms | 54.89 | 61.46 | 1.12x | 501.03 / 504.34 ms | 503.50 / 508.41 ms |

The relative throughput benefit falls from 3.93x at 10 ms to 1.35x at 100 ms and 1.12x
at 500 ms in this finite-window workload. At fixed concurrency, longer asynchronous I/O
makes the external wait account for more of each execution slot's lifetime. The remaining
prefix benefit includes the cost of repeatedly filling and draining the 32-call window;
it is not a steady-state capacity estimate.

The default recommendation remains direct dispatch for genuine asynchronous I/O. The
choice of execution mode should consider the measured synchronous prefix, actual call
latency, intended concurrent batches, connection limits and natural batch size together.
For long calls, size concurrency and backpressure from downstream capacity and the latency
target; a new scheduling mode alone cannot restore a call rate lost to longer service times.

## Validation, limitations and reproduction

All 48 client processes completed successfully: 144 measured iterations, 9,216 downstream
calls and 294,912 logical requests, with response counts and exactly-once handler visits
validated. The two probe projects built without warnings/errors, formatting passed for
changed C# files, and the full existing unit-test project passed 102/102. Production
scheduling code and public configuration were not changed.

[Raw call timings](measurements.jsonl), [aggregated results](summary.json) and
[source/environment metadata](environment.json) are retained here. Pooled P99 has 384
samples per case/mode, still too few to characterize rare production events. The experiment
uses controlled delays, loopback HTTP, two fresh client launches and finite full-batch
windows. It does not establish behavior for multi-second calls, variable service-time
distributions, TLS, retries, server rate limits or database locking. These controlled
results should be revisited using the target application's actual latency distribution.

```bash
python3 scripts/scheduling-evaluation/run_io.py \
  --latency-sweep --delays 10 100 500 --launches 2 \
  --measured-requests 2048 --warmup-requests 1024 --iterations 3 \
  --output artifacts/http-latency-evaluation
```

See the [probe instructions](../../../scripts/scheduling-evaluation/README.io.md) for
NuGet-cache settings and timing definitions. The local server is stopped automatically.
