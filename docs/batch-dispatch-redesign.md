# Parallel Batch Dispatch Redesign

## Status

Approved implementation design. This document records the intended runtime
contract before the implementation changes and the benchmark comparison used to
evaluate them.

## Problem

The previous topology tied the BufferQueue partition count and pull-consumer
count directly to `MaxConcurrency`:

```text
partitions = MaxConcurrency
pull consumers = MaxConcurrency
manual commit after handler completion
```

That makes a large `MaxConcurrency` create an equally large number of queue
partitions and consumer loops. It also retains pulled batches until their
handlers finish. A slow handler therefore holds its partition's consumer and
can make concurrency and memory behavior depend on partition skew rather than
on the configured execution limit.

## Goals

- Bound handler-batch concurrency by `MaxConcurrency` without creating the
  same number of BufferQueue partitions.
- Do not pull a BufferQueue batch when no handler execution slot is available.
- Do not add an application-owned handoff queue between BufferQueue and handler
  execution.
- Permit work from any readable partition to use any free execution slot.
- Keep accepted request completion, caller-cancellation, failure, and shutdown
  semantics exact.
- Treat `MaxPendingRequests` as queue-resident capacity, rather than a combined
  count of queued and executing requests.

## Non-goals and Contract Changes

RequestBatcher does not promise request ordering. This includes
`MaxConcurrency = 1`: concurrent callers have no stable append order, and a
partition key is a routing hint only. `UsePartitionKey` continues to select a
deterministic BufferQueue partition for equal keys, but it does not guarantee
serial handler execution or per-key ordering.

The queue uses BufferQueue auto-commit. Once a batch has been pulled, it is no
longer queue-resident and cannot be replayed by the queue. RequestBatcher still
finishes every accepted request exactly once from the handler result, caller
cancellation before dispatch, or an infrastructure failure.

## Topology

At service registration, RequestBatcher resolves a fixed internal partition
count:

```text
effectivePartitionCount = min(MaxConcurrency, max(1, Environment.ProcessorCount))
```

`Environment.ProcessorCount` is the process-visible logical-core count, so
container CPU limits are respected where the runtime exposes them. The count is
fixed for the coordinator lifetime; changing processor availability at runtime
does not reshape a live BufferQueue topic.

BufferQueue has `effectivePartitionCount` memory partitions. RequestBatcher
creates one pull consumer for the group, allowing that consumer to own all
partitions. BufferQueue chooses the readable partition for each pull, which
lets one global execution window balance work across partitions instead of
reserving one handler task per partition.

```text
producers -> BufferQueue P partitions -> BatchDispatchLoop -> H handler batches
                                         (only pulls with a free slot)

P = effectivePartitionCount
H = MaxConcurrency
```

`BatchSize` remains the BufferQueue pull batch size and the maximum number of
items passed to one handler invocation. It is not multiplied by `H`.

## BatchDispatchLoop

`BatchDispatchLoop` is the queue-facing scheduling loop. Its implementation
uses a semaphore with `H` permits and an in-flight completion tracker.

1. Wait for one execution permit.
2. Advance the BufferQueue async enumerator once. The consumer has
   `AutoCommit = true`, so this succeeds only after queue progress has advanced.
3. Hand BufferQueue's memory-topic `IReadOnlyList` snapshot directly to an
   execution task. An arbitrary enumerable is materialized only as a defensive
   fallback before that handoff.
4. Start one handler-batch task and immediately return to step 1.
5. The execution task completes each request from the handler outcome, removes
   itself from the in-flight tracker, and releases its permit in `finally`.

There is no `Task.WhenAll` in the normal dispatch path and no per-superbatch
barrier. A slow handler holds only its own execution permit; batches already
running in other slots complete normally, and the loop can dispatch another
batch as soon as any slot becomes free. The in-flight tracker is used only to
observe terminal drain and task failures without creating a second queue.

The permit is acquired before advancing the BufferQueue enumerator. With all
handler slots occupied, the loop does not consume more work. The bounded
BufferQueue then holds up to `MaxPendingRequests` queued requests and applies
the configured wait/fail admission behavior directly.

## Lifecycle and Errors

- A request cancelled before dispatch is finished as cancelled and is never
  passed to the handler.
- Once a batch starts handler dispatch, caller cancellation does not replace the
  actual handler result.
- A handler exception completes every active request in that batch with the
  same exception, releases its slot, and does not terminate the dispatch loop.
- An enumeration, auto-commit, or other queue infrastructure failure terminates
  the loop. The coordinator fails every queued request, stops admission, and
  reports the same failure from `StopAsync` and `DisposeAsync`.
- `StopAsync` first stops admission, waits until all accepted requests have a
  terminal result, then cancels the dispatch loop. A loop blocked waiting for an
  execution permit observes this cancellation. Running handlers are allowed to
  report their actual outcome before shutdown completes.

## Tests

The implementation must add or adjust deterministic tests for:

- one global consumer and auto-commit configuration;
- partition-count capping at the process-visible core count;
- no additional pull while all execution slots are occupied;
- a slow batch not blocking a later batch that can use a different free slot;
- queue backpressure while every execution slot is occupied;
- handler failure, enumeration failure, cancellation, and stop/drain behavior;
- removal of ordering guarantees from the public contract and tests.

## Benchmark Method

The rate benchmark fixes `BatchSize = 100`, uses paced single-request
submissions for three seconds, and varies `TargetQps` over 100, 1,000, and
5,000 and `MaxConcurrency` over 1, 4, 16, and 64. Direct PostgreSQL SELECTs
use the same request stream, connection-pool limit, and result validation.

Both runs use:

```bash
dotnet run --project tests/RequestBatcher.Benchmarks/RequestBatcher.Benchmarks.csproj \\
  --configuration Release --no-build --no-restore -- \\
  --filter '*PostgreSqlPriceQueryRateBenchmarks*' \\
  --warmupCount 1 --iterationCount 3 --invocationCount 1 --unrollFactor 1 \\
  --stopOnFirstError --disableLogFile
```

The mean is elapsed time for the fixed three-second workload, and `Allocated`
is managed allocation for one benchmark invocation. It is not process RSS.

### Before Implementation

Environment: macOS Sequoia 15.7.9, Apple M2 Max (12 logical cores), .NET 8.0.25
host, PostgreSQL 17.6 container, BenchmarkDotNet 0.15.8. The baseline was
re-run from an isolated clone of the pre-implementation commit. It used the
old manual-commit topology where partitions and pull consumers both equalled
`MaxConcurrency`.

| Target QPS | Max concurrency | Direct mean | Direct allocated | Direct GC (G0/G1/G2) | Batcher mean | Batcher allocated | Batcher GC (G0/G1/G2) |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 100 | 1 | 3.008 s | 770.16 KB | 0 / 0 / 0 | 3.010 s | 1,586.59 KB | 0 / 0 / 0 |
| 100 | 4 | 3.004 s | 771.18 KB | 0 / 0 / 0 | 3.010 s | 1,717.61 KB | 0 / 0 / 0 |
| 100 | 16 | 3.008 s | 769.69 KB | 0 / 0 / 0 | 3.009 s | 1,718.78 KB | 0 / 0 / 0 |
| 100 | 64 | 3.007 s | 768.27 KB | 0 / 0 / 0 | 3.007 s | 1,711.16 KB | 0 / 0 / 0 |
| 1,000 | 1 | 3.006 s | 7,474.01 KB | 0 / 0 / 0 | 3.005 s | 2,522.81 KB | 0 / 0 / 0 |
| 1,000 | 4 | 3.008 s | 7,622.41 KB | 0 / 0 / 0 | 3.005 s | 6,745.22 KB | 0 / 0 / 0 |
| 1,000 | 16 | 3.011 s | 8,115.38 KB | 1,000 / 0 / 0 | 3.009 s | 16,906.44 KB | 2,000 / 1,000 / 0 |
| 1,000 | 64 | 3.008 s | 8,134.66 KB | 1,000 / 0 / 0 | 3.008 s | 16,929.46 KB | 2,000 / 1,000 / 0 |
| 5,000 | 1 | 3.009 s | 36,944.58 KB | 4,000 / 1,000 / 0 | 3.005 s | 7,612.33 KB | 0 / 0 / 0 |
| 5,000 | 4 | 3.007 s | 37,165.25 KB | 4,000 / 1,000 / 0 | 3.008 s | 12,513.35 KB | 1,000 / 0 / 0 |
| 5,000 | 16 | 3.011 s | 39,320.76 KB | 4,000 / 1,000 / 0 | 3.007 s | 30,191.54 KB | 3,000 / 1,000 / 0 |
| 5,000 | 64 | 3.058 s | 41,144.11 KB | 5,000 / 1,000 / 0 | 3.114 s | 82,641.92 KB | 10,000 / 3,000 / 0 |

GC cells record G0/G1/G2 collections per 1,000 benchmark operations in that
order. BenchmarkDotNet omits a generation column when every measurement is
zero, so omitted values are recorded explicitly as `0` above. At 5,000 QPS and
`MaxConcurrency = 64`, the batcher allocated approximately 83 MB per
invocation and collected G0/G1 10,000/3,000 times. This is the primary
memory-pressure case the redesigned topology must remeasure.

### After Implementation

The identical command completed all 24 direct and RequestBatcher benchmark
cases. Every benchmark invocation validated its query results, and all
temporary PostgreSQL containers were removed after the run.

| Target QPS | Max concurrency | Direct mean | Direct allocated | Direct GC (G0/G1/G2) | Batcher mean | Batcher allocated | Batcher GC (G0/G1/G2) |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 100 | 1 | 3.008 s | 759.14 KB | 0 / 0 / 0 | 3.008 s | 1,449.26 KB | 0 / 0 / 0 |
| 100 | 4 | 3.009 s | 760.14 KB | 0 / 0 / 0 | 3.006 s | 1,532.16 KB | 0 / 0 / 0 |
| 100 | 16 | 3.006 s | 760.08 KB | 0 / 0 / 0 | 3.007 s | 1,548.80 KB | 0 / 0 / 0 |
| 100 | 64 | 3.008 s | 769.38 KB | 0 / 0 / 0 | 3.009 s | 1,543.40 KB | 0 / 0 / 0 |
| 1,000 | 1 | 3.011 s | 7,375.47 KB | 0 / 0 / 0 | 3.006 s | 2,474.04 KB | 0 / 0 / 0 |
| 1,000 | 4 | 3.009 s | 7,558.59 KB | 0 / 0 / 0 | 3.006 s | 5,767.06 KB | 0 / 0 / 0 |
| 1,000 | 16 | 3.007 s | 8,167.21 KB | 1,000 / 0 / 0 | 3.007 s | 14,698.63 KB | 1,000 / 0 / 0 |
| 1,000 | 64 | 3.009 s | 8,163.22 KB | 1,000 / 0 / 0 | 3.005 s | 14,738.99 KB | 1,000 / 0 / 0 |
| 5,000 | 1 | 3.011 s | 37,074.77 KB | 4,000 / 1,000 / 0 | 3.010 s | 7,686.80 KB | 0 / 0 / 0 |
| 5,000 | 4 | 3.010 s | 37,096.82 KB | 4,000 / 1,000 / 0 | 3.005 s | 10,824.47 KB | 1,000 / 0 / 0 |
| 5,000 | 16 | 3.007 s | 38,161.20 KB | 4,000 / 1,000 / 0 | 3.010 s | 20,384.21 KB | 2,000 / 1,000 / 0 |
| 5,000 | 64 | 3.041 s | 40,934.16 KB | 5,000 / 1,000 / 0 | 3.011 s | 20,354.11 KB | 2,000 / 1,000 / 0 |

The fixed three-second workload remains throughput-bound, so elapsed means are
all approximately three seconds. The redesign substantially lowers managed
allocation and GC pressure at high concurrency: at 5,000 QPS and
`MaxConcurrency = 64`, the batcher drops from 82,641.92 KB before the change
to 20,354.11 KB after it (-75.4%), while G0/G1 falls from 10,000/3,000 to
2,000/1,000. At 5,000 QPS and `MaxConcurrency = 16`, allocation drops from
30,191.54 KB to 20,384.21 KB (-32.5%) and G0 falls from 3,000 to 2,000.

## Acceptance Criteria

- The topology follows the partition formula and creates exactly one pull
  consumer.
- No batch is pulled while the execution window is full.
- No internal application queue or superbatch `Task.WhenAll` joins handler
  dispatches.
- Public documentation describes the revised ordering and capacity semantics.
- Unit tests cover the stated lifecycle and concurrency contracts.
- The post-change benchmark table is recorded beside this baseline before the
  change is committed.
