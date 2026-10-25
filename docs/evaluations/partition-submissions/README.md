# Submission APIs and partition count

**Single-machine simulation, for reference only. Re-test with your own deployment,
request distribution and real downstream service before selecting production settings.**

This evaluation contains two independent experiments: concurrent single-request
submissions and explicit groups. Each experiment varies partition count under a
fixed handler batch size and concurrency limit and uses its own single-partition
result as the reference. The two submission APIs are not each other's control.
It uses the public response
API and checks every response, including order within each explicit submission.
The production scheduling implementation is unchanged.

## Results

Measured on 2026-10-26 with production source at `9be066a` (plus comment-only scope
documentation changes), BufferQueue 2.1.0, Apple M2 Max, macOS 27.0.1, SDK 10.0.100,
.NET 10.0.0 Arm64, and BenchmarkDotNet 0.15.8. Each API ran separately at
`BatchSize = 64`, `MaxConcurrency = 4`, Partition 1/4. All 12 cases and 24 process
launches succeeded. Their diagnostic cohorts validated **2,713 waves and 1,220,448
responses**, including per-item values, explicit group order and exactly-once
handling. Every configuration reached peak Handler concurrency four, including
the single-partition configurations.

### Experiment 1: ExecuteAsync(request)

Each call submits exactly one item, so Execute calls/s equals item throughput.
Errors below are BenchmarkDotNet's 99.9% confidence-interval half-widths for mean
wave time. In-process QPS counts Execute calls and is derived from the mean.

| Workload | Partitions | Wave time ± error (ms) | Execute calls/s | Items/s | Allocated B/item |
| --- | ---: | ---: | ---: | ---: | ---: |
| SmallBurst | 1 | 17.73 ± 0.774 | 5,415 | 5,415 | 446.83 |
| SmallBurst | 4 | 25.72 ± 0.764 | 3,733 | 3,733 | 488.43 |
| Backlog | 1 | 210.44 ± 3.055 | 19,464 | 19,464 | 376.75 |
| Backlog | 4 | 213.36 ± 5.245 | 19,198 | 19,198 | 375.34 |
| RepeatedKeys | 1 | 248.36 ± 1.805 | 16,492 | 16,492 | 376.58 |
| RepeatedKeys | 4 | 209.13 ± 2.071 | 19,586 | 19,586 | 352.32 |

![Single-submission Execute throughput](../../assets/partition-submission-single.svg)

| Workload | Partitions | Mean batch size | Handler calls/wave | Queried items/wave | Handler batches/s |
| --- | ---: | ---: | ---: | ---: | ---: |
| SmallBurst | 1 | 27.32 | 3.51 | 96 | 197.85 |
| SmallBurst | 4 | 14.52 | 6.61 | 96 | 253.69 |
| Backlog | 1 | 63.64 | 64.37 | 4,096 | 300.67 |
| Backlog | 4 | 62.73 | 65.30 | 4,096 | 300.65 |
| RepeatedKeys | 1 | 63.87 | 64.13 | 3,230.93 | 254.45 |
| RepeatedKeys | 4 | 63.05 | 64.97 | 1,801.40 | 303.51 |

Small bursts lose throughput because four partitions produce more partial batches
with the same four execution slots. Backlog results overlap within the time
intervals and show no established advantage. RepeatedKeys benefits from less
duplicate querying under its item-dependent delay model, despite slightly more
Handler calls. These are within-API comparisons against one partition.

Data: [BenchmarkDotNet CSV](single/benchmark.csv), [numeric summary](single/summary.csv),
[original process diagnostics](single/diagnostics.jsonl).

### Experiment 2: ExecuteAsync(requests)

Each SmallBurst call contains 12 items; Backlog and RepeatedKeys calls contain 32.
This experiment uses its own one-partition baseline. The first experiment is not
a control for these QPS values.

| Workload | Partitions | Wave time ± error (ms) | Execute calls/s | Items/s | Allocated B/item |
| --- | ---: | ---: | ---: | ---: | ---: |
| SmallBurst | 1 | 15.55 ± 0.995 | 514 | 6,174 | 369.39 |
| SmallBurst | 4 | 25.97 ± 0.438 | 308 | 3,697 | 451.63 |
| Backlog | 1 | 209.20 ± 1.395 | 612 | 19,579 | 274.10 |
| Backlog | 4 | 209.54 ± 1.534 | 611 | 19,548 | 296.69 |
| RepeatedKeys | 1 | 249.83 ± 3.060 | 512 | 16,395 | 273.76 |
| RepeatedKeys | 4 | 209.28 ± 3.064 | 612 | 19,572 | 280.06 |

![Explicit-submission Execute throughput](../../assets/partition-submission-explicit.svg)

| Workload | Partitions | Mean batch size | Handler calls/wave | Queried items/wave | Handler batches/s |
| --- | ---: | ---: | ---: | ---: | ---: |
| SmallBurst | 1 | 32.65 | 2.93 | 96 | 191.77 |
| SmallBurst | 4 | 15.59 | 6.06 | 96 | 231.34 |
| Backlog | 1 | 64.00 | 64.00 | 4,096 | 302.93 |
| Backlog | 4 | 63.47 | 64.53 | 4,096 | 300.61 |
| RepeatedKeys | 1 | 64.00 | 64.00 | 3,228.67 | 254.55 |
| RepeatedKeys | 4 | 63.44 | 64.57 | 1,799.03 | 298.40 |

Four partitions again increase small-burst Handler calls. In Backlog they do not
establish a timing benefit and allocate more per item. In RepeatedKeys they retain
nearly full batches, query fewer distinct items and finish sooner under the chosen
model, while allocation still rises slightly. Explicit groups do not prevent
partition-induced batch fragmentation or guarantee fewer Handler calls.

Data: [BenchmarkDotNet CSV](explicit/benchmark.csv), [numeric summary](explicit/summary.csv),
[original process diagnostics](explicit/diagnostics.jsonl).

### Configuration guidance

Start with one partition when all requests can share the same Handler work. A
single partition already reaches all four execution slots. Increase partition
count for measured key-locality benefits, such as fewer repeated query items, and
check that smaller batches do not erase those gains. Routing does not provide
per-key serialization, exclusive execution or a guarantee to collect every copy
of a key into one batch.

Keep request-item throughput, Execute calls/s and Handler batch QPS separate.
Four slots and a measured 13 ms Handler duration can support roughly
300 Handler batches/s when continuously occupied. Processing about 64 items per
batch can therefore produce roughly 19,000 items/s in this synthetic backlog;
production settings still require headroom and validation in the target environment.

The single-submission experiment emitted one BenchmarkDotNet
`MultimodalDistribution` warning; the explicit experiment emitted none. Scheduling,
timer granularity and partial batch formation affect these finite waves, so small
timing differences should not be treated as improvements. High-priority process
scheduling was unavailable in both runs. Charts show derived means; the tables
retain time uncertainty. The final runs took approximately 64 seconds (single)
and 63 seconds (explicit), including their generated builds. Earlier exploratory
configurations are not included in these results.

Validation passed the Release solution build, formatting verification for changed
project C# files, and all 483 unit tests on .NET 10. The C#
summarizer accepted both complete experiments and rejected an incomplete diagnostic
log instead of emitting a result. No Docker containers or external services were used.

## Workloads

One request represents a query for one product. A wave is a complete round of
submissions from all eight producers, followed by waiting for every response.
The 96/4,096 counts are total requests in a wave. Single submission therefore
makes 96/4,096 Execute calls per wave; explicit submission makes 8/128 calls,
containing 12/32 requests per call respectively. Each Handler invocation still
processes at most 64 requests, and at most four Handler batches run concurrently.
In RepeatedKeys, 128 different products each receive 32 input requests; actual
downstream query work is counted after deduplication within each Handler batch.

Every configuration uses `BatchSize = 64`, `MaxConcurrency = 4`, eight producers,
`MaxPendingRequests = 8192`, and wait mode. Partition counts are 1 and 4.
One partition is the default without keyed routing; larger counts route by product
ID. The comparison therefore includes enabling the partition selector, not only
changing the number of queues. Tracing and metrics listeners are disabled.

| Workload | Requests per wave | Product IDs | Simulated downstream call |
| --- | ---: | --- | --- |
| SmallBurst | 96 | All distinct | Fixed 12 ms per Handler batch |
| Backlog | 4,096 | All distinct | Fixed 12 ms per Handler batch |
| RepeatedKeys | 4,096 | 128 IDs, each repeated 32 times and deterministically shuffled | `8 + ceil(distinct IDs in batch / 8)` ms |

The first two workloads isolate batch fragmentation under a fixed downstream cost.
The third models a service whose latency increases with the number of unique items
queried. That cost model deliberately tests when key locality can help; it is not
a measured property of an external API. Every Handler deduplicates the product IDs
within its own batch and makes exactly one simulated downstream call. There is no
cross-batch cache, request collection timer, forced batch fill, or network service.

Each producer owns an equal contiguous slice of the same prepared request list.
In the single-request case it starts all calls for its slice before awaiting their
responses. In the explicit case it starts groups of at most 32 items before awaiting
all groups. SmallBurst therefore submits eight groups of 12; the larger workloads
submit 128 groups of 32. Both cases use eight `Task.Run` producer tasks; no task is
started per item to schedule the producer. Explicit submission is not one Handler
call: partitions and `BatchSize` can split it, and different submissions can merge.

A timed operation is one finite wave, from resetting the completed previous wave
through submission, all responses, and correctness validation. Provider creation,
request generation, explicit-group preparation and initial warm-up of the selected
API are outside measurement. Batches are formed
naturally while producers and the dispatch loop run concurrently. No claim is made
that a small wave always becomes one full batch in the single-partition case.

BenchmarkDotNet uses two process launches per case, three warm-up iterations, eight
measured iterations and a target iteration time of 300 ms. Each experiment has six
cases, for 12 cases in total. They run separately and retain separate result files.
No other builds, tests or benchmarks run alongside measurement.

## Correctness and measurement boundaries

Every response is checked against its originating request ID and product ID.
Explicit submission also verifies result count and positional mapping. Every
request must be handled exactly once; actual batch sizes must stay within 1–64,
active handlers must return to zero, and peak concurrency must not exceed four.

Benchmark means/errors and allocations describe one wave, including the public
API and the benchmark's producer tasks and correctness instrumentation. Derived
throughput is requests per wave divided by mean elapsed time; it is not an open-loop
saturation limit. Item throughput counts request items; Execute throughput counts
public API calls. One single call carries one item, whereas each explicit call
carries 12 items in SmallBurst or 32 in the larger workloads. Handler batch QPS is
calculated from aggregate diagnostic calls and aggregate diagnostic wave duration.
The report does not measure individual-request P95/P99 latency,
server CPU, real HTTP connection pools, or persistent backpressure.

Handler diagnostics aggregate all validated waves in each process, including
setup warm-up, BenchmarkDotNet warm-up and diagnostic invocations. They explain
batch formation and downstream work but are not additional timing samples for
BenchmarkDotNet's mean or confidence interval. Allocation is total allocated bytes,
not peak/live memory. The fixed set of partition counts and one batch/concurrency
configuration do not establish a universally optimal partition count.

## Reproduce

Source: [PartitionSubmissionBenchmarks.cs](../../../tests/RequestBatcher.Benchmarks/InMemory/PartitionSubmissionBenchmarks.cs).
Run from the repository root; no Docker or external service is required.

```bash
dotnet build tests/RequestBatcher.Benchmarks/RequestBatcher.Benchmarks.csproj \
  --configuration Release --no-restore

dotnet run --project tests/RequestBatcher.Benchmarks/RequestBatcher.Benchmarks.csproj \
  --configuration Release --no-build --no-restore -- \
  --filter '*PartitionSubmissionBenchmarks.SingleSubmissions*' \
  --artifacts /private/tmp/requestbatcher-partition-single-net10 \
  > /private/tmp/requestbatcher-partition-single-net10.log 2>&1

dotnet run --project tests/RequestBatcher.Benchmarks/RequestBatcher.Benchmarks.csproj \
  --configuration Release --no-build --no-restore -- \
  --filter '*PartitionSubmissionBenchmarks.ExplicitSubmissions*' \
  --artifacts /private/tmp/requestbatcher-partition-explicit-net10 \
  > /private/tmp/requestbatcher-partition-explicit-net10.log 2>&1

dotnet run --file scripts/SummarizePartitionSubmissions.cs -- \
  /private/tmp/requestbatcher-partition-single-net10/results/RequestBatcher.Benchmarks.InMemory.PartitionSubmissionBenchmarks-report.csv \
  /private/tmp/requestbatcher-partition-single-net10.log \
  docs/evaluations/partition-submissions/single

dotnet run --file scripts/SummarizePartitionSubmissions.cs -- \
  /private/tmp/requestbatcher-partition-explicit-net10/results/RequestBatcher.Benchmarks.InMemory.PartitionSubmissionBenchmarks-report.csv \
  /private/tmp/requestbatcher-partition-explicit-net10.log \
  docs/evaluations/partition-submissions/explicit
```

The summarizer rejects mixed-API inputs, incomplete case matrices, missing process
records, failed correctness flags and inconsistent batch/request counts. It retains
the original diagnostic JSON and generates numeric CSV suitable for plotting.
