# Partition-key submission comparison

## Workload contract

Compare one explicit `ProcessAsync(requests)` submission with issuing
`ProcessAsync(request)` for every item and then awaiting `Task.WhenAll`. Each
operation uses the same ordered integer IDs, with 10, 100, or 1,000 requests.
Single submission means concurrent outstanding calls, not awaiting each item
before issuing the next. Both methods await all work and validate every ID was
processed exactly once.

Both methods use `UsePartitionKey(request => request % 64, partitionCount: 4)`.
Equal keys route consistently; an explicit group may span several partitions
and several handler invocations. No execution ordering is assumed. The handler
batch limit stays at 100, concurrency at 8, and pending capacity at 2,048. Input
size is independent of the handler batch limit.

The handler asynchronously delays once per actual invocation with `Task.Delay`
for either 1 ms or 10 ms, then records each processed ID. This models a fixed
per-call downstream wait. It does not model per-item CPU cost, a real network,
or deduplication. OS timer resolution and scheduling can extend the delay.
There is no gate forcing submissions to accumulate before dispatch: natural
batch formation is part of this comparison.

## Measurement

DI registration, resolution, input creation, and async disposal are outside the
measured methods. Each measured operation includes resetting validation state,
submission, waiting, and validating all item counts. The single-call task array
and `Task.WhenAll` are included; the explicit path awaits its shared completion.
`MemoryDiagnoser` reports allocation per complete operation. The single path is
the baseline for each matched input-size/delay pair.

Use two launches, five warmups, twelve measured iterations, and a 300 ms
iteration target on .NET 8. These are whole-wave timings, not per-item or p99
latencies. Benchmark source changes only; public API and production behavior
are unchanged. Build, format, and the full unit suite verify integration; each
benchmark operation independently validates no missing or duplicate work.

```bash
dotnet run --project tests/RequestBatcher.Benchmarks/RequestBatcher.Benchmarks.csproj \
  --configuration Release -- \
  --filter '*PartitionKeySubmissionBenchmarks*'
```

## Measured results

Measured commit: `0db70cc3c2173061f1342f41584c4a1079344e37`.
Benchmark source SHA-256:
`3e44a283a6be12a21f70e7cd26946ea1b0b657fdff5f122dc656e91a5b7a45b0`.

Environment: ARM64 macOS 27.0.1, .NET SDK 10.0.100, .NET runtime 8.0.25,
BenchmarkDotNet 0.15.8. CPU model discovery and elevated process priority were
unavailable. No other local builds or benchmarks ran during measurement.

All 12 cases (24 process launches) completed successfully. Every measured
operation validated each input exactly once. All times and allocations below
are per complete input wave; KB means 1,024 bytes. Percentage changes are
calculated from the displayed means, not BenchmarkDotNet's ratio distribution.

| Requests | Handler delay (ms) | Singles (ms) | Explicit (ms) | Time decrease | Singles (KB) | Explicit (KB) | Allocation decrease |
|---:|---:|---:|---:|---:|---:|---:|---:|
| 10 | 1 | 1.228 | 1.181 | 3.8% | 10.76 | 9.08 | 15.6% |
| 10 | 10 | 11.023 | 11.069 | -0.4% | 10.17 | 9.08 | 10.7% |
| 100 | 1 | 2.291 | 1.257 | 45.1% | 44.59 | 23.22 | 47.9% |
| 100 | 10 | 17.050 | 11.137 | 34.7% | 40.16 | 23.22 | 42.2% |
| 1,000 | 1 | 3.707 | 2.707 | 27.0% | 285.69 | 175.57 | 38.5% |
| 1,000 | 10 | 31.610 | 22.457 | 29.0% | 282.59 | 175.09 | 38.0% |

For 10 requests, elapsed time is close and the 10 ms delay pair has overlapping
confidence intervals. At 100 and 1,000 requests, explicit submission reduces
elapsed time and allocation in this workload. Both paths still partition each
item by key; one explicit submission is not one handler invocation.

The explicit API shares completion across its items and avoids the single
path's per-item completion tasks, caller task array, and `Task.WhenAll` join.
Timing additionally includes enqueueing and natural handler batch formation.
The benchmark does not count handler invocations, so it does not isolate how
much of the time change comes from fewer downstream waits versus submission
and completion overhead. The configured delay is per actual handler invocation,
not per request.

The single-submission 100-request/10 ms case has a broad confidence interval;
BenchmarkDotNet also warns of a multimodal distribution for the single-submission
1,000-request/10 ms case. Scheduling and batch formation vary between waves.
The table is an observation of these specific settings, not a fixed gain for
production traffic or evidence of behavior under sustained capacity pressure.

Full confidence intervals, GC statistics, ratios, and environment metadata:
[BenchmarkDotNet summary](RequestBatcher.Benchmarks.InMemory.PartitionKeySubmissionBenchmarks-report-github.md)
and [CSV](RequestBatcher.Benchmarks.InMemory.PartitionKeySubmissionBenchmarks-report.csv).

Formatting verification, the Release solution build, and all 116 unit tests on
each of .NET 8 and .NET 10 pass. The measured runtime is .NET 8 only; the additional
.NET 10 correctness run is not a runtime performance comparison.
