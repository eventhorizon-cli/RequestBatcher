# Dispatch scheduling with external asynchronous I/O

The [single-call latency follow-up](../external-io-latency/README.md) measures actual HTTP,
server and handler durations at longer service delays. The 1/10/50 ms values below are
configured server waits, not measured per-call latency.

Evaluated 2026-10-09 with BufferQueue 2.0.3 and the current unkeyed single-partition
policy. This report evaluates RequestBatcher issue #5; it does not introduce a new
production execution mode. The earlier [synthetic evaluation](../bufferqueue-2.0.3/README.md)
used `Task.Delay` for its I/O workload. This follow-up sends real HTTP requests to a
separate local server process.

## How the current scheduler handles I/O

`BatchDispatchLoop.StartExecution` calls `ProcessBatchAsync` directly. The call runs
synchronously until completion or its first incomplete await. At that await, control
returns to the pull loop, which can use another free execution slot. The waiting batch
retains its slot until the whole handler completes, so `MaxConcurrency` already bounds
concurrent asynchronous operations. One unkeyed partition can supply all these batches.

This distinguishes three parts of a mixed handler:

1. Synchronous preparation before the first incomplete await runs on the dispatch path.
   Deduplication, sorting, serialization, SDK setup and synchronous connection acquisition
   can delay the next pull. Writing `async` does not move this work to another thread.
2. An incomplete asynchronous HTTP/database wait releases the dispatch call path. An
   additional `Task.Run` hop does not increase the connection pool or downstream capacity.
3. Work after that incomplete await normally runs on a continuation and can overlap other
   batches already. If every preceding await completes synchronously, that work remains
   on the original dispatch path instead. Cache hits and warmed APIs can change this boundary.

The sample's `PriceUpdateHandler` groups requests before `OpenConnectionAsync`. A pooled
connection may be available synchronously, so parameter construction can also remain on
the dispatch path until the database operation actually yields. Whether this is material
requires measuring those operations, not merely classifying the handler as an I/O handler.

These mechanics agree with Microsoft's guidance to use asynchronous APIs directly for
[I/O-bound work](https://learn.microsoft.com/en-us/dotnet/csharp/asynchronous-programming/async-scenarios)
and to diagnose [ThreadPool starvation](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/debug-threadpool-starvation)
when synchronous calls occupy workers.

## Method and scope

The [reproduction instructions](../../../scripts/scheduling-evaluation/README.io.md)
describe the full matrix and metric definitions. Each variant uses the real coordinator
and queue in an isolated source copy. The only scheduling difference is direct execution
versus `Task.Run` after acquiring the existing execution permit. Both use one partition,
`BatchSize=32`, a queue capacity of 8,192, and a closed-loop producer window of 1,024 requests.

The independent Kestrel server asynchronously waits 1, 10, or 50 ms per call. Each handler
sends one HTTP/1.1 call for its batch using a reused, prewarmed `HttpClient` connection pool.
Every response echoes its logical item count. The client verifies each request is handled
once, and checks the server's call count and total item count after every iteration.
CPU variants execute fixed counts of dependent integer operations before or after HTTP;
blocking controls use `HttpClient.Send`.

There are 12 scenarios, two modes and three fresh client processes per pair: 72 launches.
Each process warms 4,096 items, then measures three iterations of 8,192 items. Execution
order is shuffled with a fixed seed. Summaries take the median within each launch, then
the median of the three launches. ThreadPool minimums are unchanged; tiered compilation
is disabled equally. The server remains in a separate process throughout the experiment.
Raw measurements and source/environment information are retained beside this report.

## Individual-submission results

The values below are medians of three launch medians. Throughput counts logical requests,
not HTTP calls. `H/C` means execution slots / maximum HTTP connections. All CPU variants
use 10 ms HTTP delay. The large CPU loop is 2,000,000 operations; the small one is 250,000.

| Handler | H/C | Direct req/s | Offload req/s | Ratio | End-to-end p99 ms, direct / offload | Mean batch, direct / offload |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Async HTTP 1 ms | 8/8 | 158,758 | 141,111 | 0.89x | 6.0 / 7.1 | 31.0 / 25.9 |
| Async HTTP 10 ms | 8/8 | 17,711 | 17,178 | 0.97x | 56.8 / 62.3 | 31.3 / 26.9 |
| Async HTTP 10 ms | 32/8 | 17,649 | 11,445 | 0.65x | 57.4 / 100.1 | 31.0 / 18.2 |
| Async HTTP 10 ms | 32/32 | 44,709 | 42,760 | 0.96x | 22.4 / 24.7 | 31.0 / 17.0 |
| Async HTTP 50 ms | 8/8 | 4,179 | 3,880 | 0.93x | 258.7 / 268.2 | 31.4 / 27.7 |
| Synchronous HTTP 10 ms | 8/8 | 2,547 | 16,953 | 6.66x | 406.8 / 63.4 | 31.3 / 27.3 |
| Synchronous HTTP 10 ms | 32/32 | 2,537 | 15,811 | 6.23x | 401.4 / 66.7 | 31.3 / 18.2 |
| Small CPU before HTTP | 8/8 | 17,683 | 16,443 | 0.93x | 57.3 / 65.0 | 31.3 / 26.8 |
| Large CPU before HTTP | 8/8 | 14,176 | 13,576 | 0.96x | 71.9 / 77.2 | 31.3 / 26.0 |
| Large CPU before HTTP | 32/32 | 14,185 | 30,894 | 2.18x | 71.8 / 34.8 | 31.3 / 18.4 |
| Large CPU after HTTP | 8/8 | 14,008 | 13,937 | 0.99x | 73.9 / 75.3 | 31.1 / 26.0 |
| Large CPU after HTTP | 32/32 | 33,946 | 29,295 | 0.86x | 31.0 / 35.6 | 31.0 / 17.1 |

The strongest positive result is the large synchronous prefix with 32 execution slots:
throughput rises from 14,185 to 30,894 requests/s and p99 falls from 71.8 to 34.8 ms.
Direct dispatch reaches only eight active handlers despite the 32-slot limit; offloading
reaches 32. The direct dispatch path is spending approximately 1.87 ms per batch in that
prefix. With only eight slots, the same prefix does **not** benefit from offloading in
this experiment. Prefix cost alone is not sufficient to choose a mode; concurrency,
I/O duration, CPU contention and batch formation also matter.

Moving those same operations after the asynchronous HTTP wait changes the result:
direct dispatch already reaches 32 active handlers, and offloading is slower. The fact
that a handler contains CPU work is therefore less informative than where that work
executes relative to the first incomplete await.

Pure asynchronous HTTP reaches the configured handler limit in both modes. With 32
slots and only eight connections, both reach just eight simultaneous server operations.
Increasing slots cannot enlarge the downstream pool. Offloading produces smaller batches
in this producer pattern, so it needs more HTTP calls for the same logical request count.
This is a full-pipeline effect, **not** a measurement of `Task.Run` overhead in isolation.

Synchronous HTTP benefits from moving blocking calls off the single dispatch path, but
still occupies ThreadPool workers. At 32 slots, the offload launch-median throughput ranges
from 15,511 to 22,808 requests/s; worker availability is variable, and the nominal 32-slot
limit does not promise 32 active workers. Prefer a genuinely asynchronous API when available.
This process-wide ThreadPool also serves continuations and unrelated application work.

Per-launch ranges and additional measurements are in [summary.json](summary.json), with
all iterations in [measurements.jsonl](measurements.jsonl). Small differences are not
universal percentage predictions. Batch sizes differ between modes; compute-per-batch
also means smaller batches incur more total CPU work for the same logical item count.

## Explicit-submission control

A second experiment submits each 1,024-item window through the explicit batch API.
It covers the five pure asynchronous HTTP cases with another 30 launches and the same
warmup/measurement counts. Both modes produce exactly 32-item handler batches. This
controls the batch fragmentation seen with individual submissions; it does not imply
that explicit submission generally guarantees a particular handler invocation boundary.

| HTTP delay | H/C | Direct req/s | Offload req/s | Ratio | Direct launch range | Offload launch range |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 1 ms | 8/8 | 175,995 | 176,806 | 1.00x | 175,229–176,348 | 176,790–180,036 |
| 10 ms | 8/8 | 21,440 | 21,484 | 1.00x | 21,383–21,554 | 21,151–21,744 |
| 10 ms | 32/8 | 21,370 | 21,532 | 1.01x | 21,312–21,426 | 21,054–21,616 |
| 10 ms | 32/32 | 87,278 | 85,716 | 0.98x | 86,619–89,195 | 85,435–86,203 |
| 50 ms | 8/8 | 4,865 | 4,857 | 1.00x | 4,836–4,870 | 4,840–4,859 |

With full batches, offload/direct ratios range from 0.98 to 1.01: there is no substantial
pure-I/O benefit. The connection-limited case recovers from a 0.65 ratio to 1.01 when
batch sizes match. This supports batch fragmentation as an important explanation for
its individual-submission regression; it should not be described as a 35% scheduling-hop
cost. With adequate connections and full batches, direct dispatch at 32 slots achieves
about four times the throughput of direct dispatch at eight slots without changing modes.

The control's end-to-end timestamps represent completion of the whole explicit submission,
not individual per-item completion, so its latency values must not be compared directly
with the first table's p99. Its [raw data](batch-control.jsonl) and
[environment](batch-control-environment.json) are retained for inspection.

## Recommendation for issue #5

Keep direct dispatch as the default for genuinely asynchronous database/HTTP handlers.
The important tuning controls remain `BatchSize`, `MaxConcurrency`, and downstream
connection/admission limits. Partition count is not the asynchronous I/O concurrency limit.
A rough sizing estimate for a stable workload is desired handler calls/second multiplied
by average handler duration in seconds; use measured mean batch size to translate from
logical request rate. This is a starting estimate, not a safe operating limit: downstream
capacity, tail latency, CPU load and queue growth must still be observed.

A separately designed, opt-in bounded ThreadPool mode remains justified for handlers
whose synchronous prefix prevents filling otherwise available execution slots, or for
unavoidable blocking integrations. Do not enable it merely because a handler calls an
external service. Keep the default explicit and stable; do not attempt to infer a mode
from an `async` signature or change it dynamically from a few observed completion times.
For a handler that uses one downstream call per batch, a scheduling change can also
change the number of downstream calls by changing natural batch formation.

Before implementing that option, define and test:

- Acquire the execution permit before scheduling; queued delegates plus executing batches
  together must stay within `MaxConcurrency`. No unbounded intermediate work queue.
- Preserve the dispatch/cancellation boundary. The performance probe schedules the entire
  `ProcessBatchAsync`, moving `TryStartProcessing` later. A production design should make
  the transition explicit before handing accepted processing work to the scheduler, or
  deliberately specify and validate another boundary. Do not silently extend cancellation
  past the point at which callers expect actual side effects to be reported.
- Never allow a scheduling cancellation token to suppress the cleanup delegate. Scheduling
  failure, caller cancellation, handler failure and infrastructure failure need exactly-once
  completion, in-flight accounting and permit release.
- Resolve scoped/transient handlers inside the executing batch's async scope, await the whole
  handler, and keep the scope alive across I/O. Stop admission before shutdown draining and
  wait for scheduled delegates as well as running handlers.
- Use deterministic gates to test cancellation before scheduling, after dispatch but before
  delegate execution, handler failure, scope disposal and concurrent stop/dispose. Retain
  direct-mode behavior tests, and add mixed fast/slow handlers and competing ThreadPool work.

## Limits and validation

These are local HTTP measurements on .NET 8.0.25 / SDK 10.0.100, Arm64, with 12 process-visible
processors. They are not measurements of a remote production dependency. The controlled
service delay uses `Task.Delay` in the server, but the client performs real HTTP/socket I/O
against that separate process. Both processes share the machine's CPU resources.

The experiment does not model TLS, large serialization payloads, remote jitter, database
locks, real transaction work, retry storms, cache-hit ratios, admission timeouts or rate
limits. Its fixed producer windows create fill/drain effects; p99 is not an open-loop
production latency forecast. The ThreadPool samples are approximately 10 ms apart and can
miss spikes, especially during starvation. No dedicated-worker isolation was evaluated.
The offload prototype's lifecycle semantics have not been validated for production.

All 102 client launches and 306 measured iterations completed with response, item-visit,
and downstream count validation (2,506,752 measured logical requests). The experiment
projects build with zero warnings/errors; new C# experiment files were formatted. The
existing complete unit-test project still passes 102/102 tests. Production scheduling
code and public options were not changed by this evaluation.

## Reproduce

```bash
python3 scripts/scheduling-evaluation/run_io.py --output artifacts/http-scheduling-evaluation
python3 scripts/scheduling-evaluation/run_io.py --batch-control --output artifacts/http-batch-control
```

Use the normal NuGet configuration. If the usual cache is not writable, set
`NUGET_PACKAGES` and `NUGET_HTTP_CACHE_PATH` to writable temporary directories. A local
HTTP listener must be permitted. The server is stopped automatically when the run ends.
