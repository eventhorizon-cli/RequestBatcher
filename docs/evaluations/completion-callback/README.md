# Reusing the pending-request completion callback

Caching one coordinator-owned completion delegate removes about **64 B per pending item**.
Tasks and pending objects are not pooled; coordinator construction is outside measurement.

Measured baseline `5043545` against `c4c9c80`, using identical benchmark source on Arm64 macOS,
SDK 10.0.100, .NET 8.0.25, workstation GC and BenchmarkDotNet 0.15.8.
Both suites use singleton handlers and validate exactly-once outcomes.

## Results

Time and allocation are per wave; KB means 1,024 bytes. The explicit suite uses 512 requests,
batch size 32, four partitions, concurrency 8, capacity 1,024, two launches, five warmups,
12 measurements and a 300 ms iteration target. Mixed waves submit half the requests singly.

| Workload | Before (us) | After (us) | Time decrease | Before (KB) | After (KB) | Allocation decrease |
|---|---:|---:|---:|---:|---:|---:|
| All explicit, group 256 | 143.40 | 136.60 | 4.7% | 124.70 | 92.76 | 25.6% |
| All explicit, group 16 | 193.20 | 187.00 | 3.2% | 142.39 | 110.43 | 22.4% |
| Half single, group 256 | 285.70 | 260.80 | 8.7% | 155.70 | 124.12 | 20.3% |
| Half single, group 16 | 291.70 | 278.90 | 4.4% | 161.48 | 129.45 | 19.8% |

The response suite uses 256 requests, batch size 32, concurrency 8, capacity 512, no key,
two launches, eight warmups, 20 measurements and a 500 ms target. Cancellation uses
concurrency 1 plus two blocker/drain requests.

| Workload | Before (us) | After (us) | Time decrease | Before (KB) | After (KB) | Allocation decrease |
|---|---:|---:|---:|---:|---:|---:|
| Response explicit success | 71.50 | 66.07 | 7.6% | 76.38 | 60.38 | 20.9% |
| Request-only single success | 203.75 | 200.82 | 1.4% | 94.08 | 77.88 | 17.2% |
| Response single success | 211.64 | 202.36 | 4.4% | 111.31 | 95.62 | 14.1% |
| Response pre-dispatch cancellation | 6,365.37 | 6,308.79 | 0.9% | 253.09 | 236.96 | 6.4% |
| Response single failure | 7,425.80 | 7,420.46 | 0.1% | 571.95 | 555.56 | 2.9% |

**Allocation reduction is the stable finding.** Small timing changes do not establish
throughput gains: request-only success and group-16 cases have overlapping 99.9% intervals,
and failure timing is effectively unchanged. Gates make callers wait; gate/reset, submission,
queueing, completion and validation costs are included equally, while inputs and DI setup are excluded.

These in-process waves do not measure network I/O, scope disposal, capacity saturation,
retained heap size or per-request tail latency. All nine benchmark cases validated successfully.

## Reproduce

```bash
dotnet run --project tests/RequestBatcher.Benchmarks/RequestBatcher.Benchmarks.csproj \
  --configuration Release -- \
  --filter '*ResponseCompletionBenchmarks*' '*ExplicitSubmissionBenchmarks*' --artifacts /private/tmp/completion-callback-results
```
