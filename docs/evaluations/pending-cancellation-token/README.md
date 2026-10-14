# Pending request cancellation-token storage

Passing the original token through the cancellation registration removes a duplicate
pending-item field. **The measured saving is 8 B per pending item**, not a removed task.
Queued cancellation keeps its original token; dispatched work reports its actual outcome.

## Results

Primitive measurements include the pending object and single-request completion/task.
The reusable cancellation source is created outside measurement, so registration nodes
may be reused; this is not a peak-live-registration measurement.

| Campaign | Cancellable | Before | After | Allocated before / after |
|---|---|---:|---:|---:|
| Initial, before then after | No | 24.55 ns | 23.91 ns | 160 / 152 B |
| Initial, before then after | Yes | 43.11 ns | 43.69 ns | 160 / 152 B |
| Repeat, after then before | No | 26.03 ns | 24.15 ns | 160 / 152 B |
| Repeat, after then before | Yes | 47.29 ns | 43.94 ns | 160 / 152 B |

Selected full-pipeline results show both campaigns and their timing variation:

| Scenario / campaign | Items/group | Before | After | Before allocated | After allocated |
| --- | ---: | ---: | ---: | ---: | ---: |
| Success / initial | 10 | 8.501 μs | 9.444 μs | 5.09 KB | 5.01 KB |
| Success / reverse-order repeat | 10 | 10.39 μs | 9.504 μs | 5.09 KB | 5.01 KB |
| Success / initial | 1,000 | 158.156 μs | 178.296 μs | 181.56 KB | 173.75 KB |
| Success / reverse-order repeat | 1,000 | 188.84 μs | 180.174 μs | 181.56 KB | 173.74 KB |
| Partial cancellation / initial | 1,000 | 580.120 μs | 626.802 μs | 471.98 KB | 464.18 KB |
| Partial cancellation / reverse-order repeat | 1,000 | 672.69 μs | 621.704 μs | 471.99 KB | 464.21 KB |
| Mixed single/group / reverse-order repeat | 10 | 30.59 μs | 29.823 μs | 11.26 KB | 11.46 KB |

Large successful groups save about 7.81 KB (4.3%). **No throughput gain is claimed:**
initial slowdowns did not reproduce in reverse order, and the baseline varied substantially.
Some async results were multimodal or had short iterations. Small mixed allocation can rise
because batch formation changes; total pipeline allocation is not determined by item count alone.

## Method and reproduction

Measured 2026-10-13, Arm64 macOS, SDK 10.0.100, .NET 8.0.25, BenchmarkDotNet 0.15.8.
Baseline `1fe5abd` (production identical to `8874d14`), candidate `f6fb2ef` / `b3a2c6d`.
Both versions use identical benchmark source. All 48 measured cases validated successfully.

Primitive: one launch, three warmups, eight measurements. Pipeline: two launches, five
warmups, 12 measurements, 300 ms target; groups of 10/1,000 cover success, failure, partial
cancellation, capacity waiting and mixed submissions. The
[shared-completion method](../shared-completion-source/README.md#method-and-reproduction)
defines the matched setup, gates and exactly-once checks. KB means 1,024 bytes.

```bash
dotnet run --project tests/RequestBatcher.Benchmarks/RequestBatcher.Benchmarks.csproj \
  --configuration Release -- \
  --filter '*PendingRequestAllocationBenchmarks*' '*ExplicitCompletionBenchmarks*' --artifacts /private/tmp/pending-token-results
```
