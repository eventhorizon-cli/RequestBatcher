# Partition-key submission comparison

Compare one explicit `ExecuteAsync(requests)` with concurrent `ExecuteAsync(request)` calls
joined by `Task.WhenAll`. Both await all items and validate each ID exactly once.

Measured `0db70cc` on Arm64 macOS 27.0.1, SDK 10.0.100, .NET 8.0.25 and BenchmarkDotNet 0.15.8.
Both paths use `UsePartitionKey(request => request % 64, partitionCount: 4)`, batch size 100,
concurrency 8 and capacity 2,048. Each handler invocation delays 1 or 10 ms; there is no
forced accumulation gate. Two launches, five warmups, 12 measurements, 300 ms iteration target.

## Results

Time and allocation are per complete input wave; KB means 1,024 bytes. DI/input setup and
disposal are excluded; submission, waiting, counter resets and validation are included.
All 12 cases across 24 launches validated successfully.

| Requests | Handler delay (ms) | Singles (ms) | Explicit (ms) | Time decrease | Singles (KB) | Explicit (KB) | Allocation decrease |
|---:|---:|---:|---:|---:|---:|---:|---:|
| 10 | 1 | 1.228 | 1.181 | 3.8% | 10.76 | 9.08 | 15.6% |
| 10 | 10 | 11.023 | 11.069 | -0.4% | 10.17 | 9.08 | 10.7% |
| 100 | 1 | 2.291 | 1.257 | 45.1% | 44.59 | 23.22 | 47.9% |
| 100 | 10 | 17.050 | 11.137 | 34.7% | 40.16 | 23.22 | 42.2% |
| 1,000 | 1 | 3.707 | 2.707 | 27.0% | 285.69 | 175.57 | 38.5% |
| 1,000 | 10 | 31.610 | 22.457 | 29.0% | 282.59 | 175.09 | 38.0% |

Explicit submission reduces per-item completion/task overhead and allocation in this workload.
It still routes each item independently and may produce several handler batches.

**Timing also includes natural batch formation.** Handler invocation counts were not measured,
so these results cannot separate fewer downstream waits from submission/completion savings.
The 10-request/10 ms intervals overlap; the single 100-request/10 ms case has a broad interval,
and the single 1,000-request/10 ms case was multimodal. These are whole-wave measurements,
not per-item p99 or sustained-capacity predictions; no real I/O or deduplication is modeled.

## Reproduce

```bash
dotnet run --project tests/RequestBatcher.Benchmarks/RequestBatcher.Benchmarks.csproj \
  --configuration Release -- \
  --filter '*PartitionKeySubmissionBenchmarks*' --artifacts /private/tmp/partition-key-submission-results
```

## Observability

The partition-key comparison does not include the built-in RequestBatcher instrumentation. See the
[observability design](../../observability-design.md) for how a submission is linked to one or more batches.
