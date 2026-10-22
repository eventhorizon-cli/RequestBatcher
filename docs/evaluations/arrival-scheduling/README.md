# Dispatch under paced arrivals

**Keep direct dispatch for the current asynchronous use cases.** Bounded
`Task.Run` offloading did not produce a consistent benefit for asynchronous
delay, PostgreSQL, or Redis handlers. It adds scheduling and allocation costs,
and can increase caller tail latency. This evaluation does not justify a new
production scheduling option or a change to the default.

Synchronous CPU work and blocking calls can hold the dispatch loop and benefit
from offloading. That result is workload-specific: moving work also changes
batch formation and downstream call volume. A future synchronous-workload
proposal should establish its own contract and lifecycle tests.

Production dispatch calls a handler directly after reserving an execution slot.
This diagnostic compares that path with an isolated `Task.Run` prototype at
multiple arrival rates. The prototype reserves the same slot before scheduling;
executing and scheduled batches together cannot exceed eight. It changes only
a temporary source copy, with no production option or runtime hook added to
the checkout.

## Results

Measured 2026-10-18 on Apple M2 Max, 12 logical processors, Arm64 macOS 27.0.1,
SDK 10.0.100 and .NET 8.0.25. The campaign completed **160 process launches**:
80 configurations, two launches each, and 40 direct/offload pairs. All
**13,064,000 logical requests** passed in-process exactly-once validation;
database cases also passed persisted-count and value checks. Final database
state does not prove exactly-once execution of external side effects.

Selected results below use the median of two per-launch measurements. Throughput
is logical requests/s; p99 is caller latency in milliseconds, including generator
lag. These are finite arrival windows, not universal capacity estimates.

| Handler | Intended rate | Batch limit | Direct throughput | Offload throughput | Direct p99 | Offload p99 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| `Task.Delay(1)` | 12,000 | 1 | 6,685 | 6,590 | 1,569.72 | 1,625.03 |
| `Task.Delay(1)` | 12,000 | 64 | 12,001 | 11,996 | 2.22 | 2.34 |
| PostgreSQL write | 30,000 | 64 | 29,997 | 29,887 | 7.03 | 6.09 |
| PostgreSQL read | 30,000 | 64 | 30,044 | 30,035 | 1.55 | 1.62 |
| Redis write | 100,000 | 64 | 99,956 | 99,955 | 2.36 | 8.85 |
| Redis read | 100,000 | 64 | 99,966 | 99,972 | 2.18 | 2.32 |
| Immediate completion | 150,000 | 1 | 149,843 | 149,860 | 3.30 | 18.68 |
| Synchronous CPU prefix | 12,000 | 1 | 9,686 | 12,005 | 477.46 | 0.12 |
| Blocking prefix | 5,000 | 1 | 860 | 5,001 | 9,529.29 | 3.38 |

In the overloaded one-item asynchronous cases, occupied-slot wall utilization
was already high: 98.8% for direct delay, 98.9% for PostgreSQL writes, and 88.9%
for Redis writes at the highest tested rate. Adding a ThreadPool hop did not
remove that occupancy. This metric includes asynchronous waiting, so it cannot
identify CPU saturation or attribute every limit to the dispatch loop.

The blocking-prefix case illustrates the different tradeoff. With a batch limit
of 64 and 12,000 intended requests/s, direct/offload mean batch sizes were
13.7/1.7 and slot utilization was 12.5%/98.5%. Both met the intended rate, while
offloading formed many more handler batches. Consequently, a latency improvement
does not by itself establish a better database or application-level tradeoff.

The highest immediate-completion case also exposes the issuer's limit: at a
batch limit of one and 300,000 intended requests/s, actual issue rates were only
about 140,000/s in both modes. Its backlog and throughput include that generator
lag and probe overhead; they do not isolate the dispatch loop's capacity.

The [complete tables](results.md), [numeric results](results.csv), and
[source and prototype hashes](environment.json) retain every configuration.
Tables show each configuration's throughput range and the median of its
per-launch latency percentiles; the CSV also retains selected min/max metrics.
The source commit records the pre-squash optimization branch; the source hash
identifies the actual measured snapshot, including uncommitted diagnostic files.

## Method

The coordinator, DI container, BufferQueue 2.0.3, admission limits, request IDs,
and handler logic are identical within each direct/offload pair. Each case is a
fresh .NET 8 process with tiered compilation disabled equally. There is one
partition, eight handler slots, batch limits 1/64, and queue capacity
`max(BatchSize * 8 * 4, 128)`. The handler warms up 1,024 logical items before
measurement. Two launches per configuration are shuffled with a fixed seed.
Delay, CPU-prefix, and blocking-prefix work occurs once per handler batch.
Direct/offload pairs match that work; different batch formation changes its
amortized cost per logical item.

The issuer plans two seconds of arrivals and submits every item even when late.
Each submission is independent of preceding completion, including submissions
waiting for queue capacity. Caller latency starts at the scheduled due time,
so generator delay and backlog are included. Throughput includes draining all
planned work; the actual issue rate uses actual submission timestamps. These
finite arrival windows locate overload in the tested configurations, rather
than establish a universal steady-state capacity or production p99.

| Handler | Intended logical requests/s | Items at highest rate |
| --- | --- | ---: |
| Immediate synchronous completion | 50,000 / 150,000 / 300,000 | 600,000 |
| `Task.Delay(1)` | 500 / 5,000 / 12,000 | 24,000 |
| 100,000 dependent integer operations before completion | 500 / 5,000 / 12,000 | 24,000 |
| `Thread.Sleep(1)` before completion | 500 / 5,000 / 12,000 | 24,000 |
| PostgreSQL read/write | 1,000 / 30,000 | 60,000 |
| Redis read/write | 1,000 / 100,000 | 200,000 |

All accepted IDs must be processed exactly once. PostgreSQL uses
`postgres:17.6-alpine`, the sample database, an eight-connection pool, and one
autocommit UNNEST upsert per handler batch. Reads use the same table and batched
ID lookup. Redis uses `redis:7.4.5-alpine`, a shared `ConnectionMultiplexer`, and
MSET/MGET per handler batch. Both modes use identical commands, values, and
commit semantics. Read cases seed the maximum item count for that database;
write cases begin with no keys/rows for the run. Persisted counts and values
are checked; Redis also verifies the key set. The run's data is then removed.
Schema creation, pool warmup, seeding, validation, reset, and provider disposal
are outside timing.

## Metrics and limits

Slot wall utilization integrates occupied execution slots over elapsed time and
divides by eight. It includes asynchronous waits and the prototype's ThreadPool
queueing, so it is not CPU utilization. Peak active slots also includes scheduled
batches and does not count simultaneously running handler bodies. Batch counts
and mean sizes expose changes in batch formation and downstream call volume.

Allocation and GC deltas cover the process during measurement, including issuer,
completion observation, and telemetry overhead. ThreadPool counters are endpoint
deltas, not queue-length traces. Both paths carry the same telemetry; its locks
can affect short handlers and tail latency. Latency percentiles are per launch;
two launches cannot characterize rare production tails or all ThreadPool states.
Each percentile uses sorted index `ceil(count * p) - 1`.
The local Docker databases omit remote latency, failover, contention, retries,
and production data distributions. Redis validation requires one standalone
endpoint; the probe does not support Redis Cluster or multiple endpoints.

The offload implementation remains a performance prototype. Moving request
start transitions into a scheduled delegate changes the cancellation window.
A production mode would require a lifecycle design and deterministic coverage
for cancellation, scheduling failure, exception fan-out, scopes, and shutdown.

## Reproduction

Run each command sequentially on an otherwise idle host with Docker available.
The PostgreSQL sample uses port 54329; use a dedicated Redis instance and set
`REQUEST_BATCHER_REDIS_CONNECTION` to its endpoint. Output directories contain
per-launch JSON and source/runner/prototype hashes. See the
[probe instructions](../../../scripts/scheduling-evaluation/README.arrival.md)
for setup and metric definitions.

```bash
dotnet run --file scripts/scheduling-evaluation/EvaluateArrivalScheduling.cs -- \
  --workloads io cpu blocking --rates 500 5000 12000 \
  --duration-ms 2000 --max-items 24000 --warmup-items 1024 --repeats 2 \
  --output /private/tmp/arrival-memory

dotnet run --file scripts/scheduling-evaluation/EvaluateArrivalScheduling.cs -- \
  --workloads fast --rates 50000 150000 300000 \
  --duration-ms 2000 --max-items 600000 --warmup-items 1024 --repeats 2 \
  --output /private/tmp/arrival-fast

dotnet run --file scripts/scheduling-evaluation/EvaluateArrivalScheduling.cs -- \
  --database postgres --database-operation write --rates 1000 30000 \
  --duration-ms 2000 --max-items 60000 --warmup-items 1024 --repeats 2 \
  --output /private/tmp/arrival-postgres-write

dotnet run --file scripts/scheduling-evaluation/EvaluateArrivalScheduling.cs -- \
  --database redis --database-operation write --rates 1000 100000 \
  --duration-ms 2000 --max-items 200000 --warmup-items 1024 --repeats 2 \
  --output /private/tmp/arrival-redis-write
```

Repeat the database commands with `--database-operation read` and separate
output directories for the read comparisons.

## Observability

This evaluation predates the built-in RequestBatcher instrumentation and does not report tracing
overhead or trace output. See the [observability design](../../observability-design.md) for the production span model.
