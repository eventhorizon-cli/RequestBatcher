# Shared explicit completion source

The shared completion derives directly from `TaskCompletionSource`, removing
one object per explicit submission. It still uses one task for all items, waits
for every item, preserves distinct exception identity and aggregation, and
publishes failure before cancellation before success. Continuations remain
asynchronous. No caller task or pending state is pooled.

## Result

Retain this as a fixed allocation reduction: **168 B to 144 B per submitted
group**, saving **24 B**, independent of group size. This is not a per-item
saving and does not establish a throughput improvement. Full-pipeline allocation
is much larger, so the reduction is small there.

Primitive measurements (128 / 1,024 / 8,192 completion notifications):

| Campaign | Before times | After times | Allocated before / after |
|---|---|---|---|
| Initial, before then after | 485.9 / 3,717.9 / 29,621.1 ns | 503.1 / 3,833.0 / 30,644.5 ns | 168 / 144 B |
| Focused repeat, after then before | 510.0 / 3,844.5 / 30,389.8 ns | 492.1 / 3,791.5 / 30,026.9 ns | 168 / 144 B |

The initial primitive timing was about 3% worse; the reverse-order repeat was
about 1–4% better. The allocation reduction reproduced in every case. Timing
is therefore reported independently, without claiming a speedup.

## Public pipeline

Initial full campaign:

| Scenario | Items/group | Before | After | Time change | Before allocated | After allocated |
|---|---:|---:|---:|---:|---:|---:|
| Explicit group observed handler failure | 10 | 231.726 μs | 232.442 μs | +0.31% | 10.6 KB | 10.59 KB |
| Explicit group partial caller cancellation | 10 | 43.061 μs | 43.144 μs | +0.19% | 8.37 KB | 8.34 KB |
| Explicit group success | 10 | 8.635 μs | 8.379 μs | -2.96% | 5.11 KB | 5.09 KB |
| Explicit groups with capacity waiting | 10 | 22.528 μs | 22.669 μs | +0.63% | 8.6 KB | 8.55 KB |
| Mixed single requests and one explicit group | 10 | 27.055 μs | 24.306 μs | -10.16% | 11.19 KB | 11.11 KB |
| Explicit group observed handler failure | 1000 | 1,708.030 μs | 1,729.805 μs | +1.27% | 215.02 KB | 214.99 KB |
| Explicit group partial caller cancellation | 1000 | 562.499 μs | 634.822 μs | +12.86% | 472.01 KB | 471.98 KB |
| Explicit group success | 1000 | 162.655 μs | 161.394 μs | -0.78% | 181.58 KB | 181.56 KB |
| Explicit groups with capacity waiting | 1000 | 331.636 μs | 348.933 μs | +5.22% | 365.95 KB | 365.93 KB |
| Mixed single requests and one explicit group | 1000 | 995.617 μs | 1,033.322 μs | +3.79% | 467.07 KB | 466.98 KB |

The 1,000-item partial-cancellation result initially rose 12.86%, from
562.499 ± 3.597 μs to 634.822 ± 77.370 μs (BDN error). Because its after-run
variance was high, partial cancellation, capacity waiting, and the primitive
were repeated in reverse version order with unchanged benchmark settings:

| Scenario | Items/group | Before | After | Time change | Before allocated | After allocated |
|---|---:|---:|---:|---:|---:|---:|
| Explicit group partial caller cancellation | 10 | 43.00 μs | 44.20 μs | +2.79% | 8.37 KB | 8.34 KB |
| Explicit groups with capacity waiting | 10 | 22.12 μs | 21.94 μs | -0.81% | 8.59 KB | 8.57 KB |
| Explicit group partial caller cancellation | 1000 | 557.53 μs | 582.94 μs | +4.56% | 472 KB | 471.99 KB |
| Explicit groups with capacity waiting | 1000 | 337.09 μs | 325.95 μs | -3.30% | 365.95 KB | 365.91 KB |

In the repeat, the large cancellation case was 557.53 ± 5.196 μs before and
582.94 ± 36.261 μs after; the error ranges overlap. The large capacity case
changed from +5.22% in the initial campaign to -3.30% in the repeat. These
measurements do not establish a repeatable material latency regression, but
also do not prove equal throughput or rule out a small regression. Cancellation
had short-iteration warnings, and some initial workloads were multimodal.
All negative results and repeat reports are retained; no timing result is used
as the reason to retain the change.

## Method and reproduction

Measured 2026-10-13 on macOS Arm64, .NET SDK 10.0.100, .NET runtime 8.0.25,
BenchmarkDotNet 0.15.8. Baseline production: `b93849a`. Candidate: `1fe5abd`.
Intervening consumer-exhaustion tests do not alter production code. Both trees
used exactly the same benchmark sources:

- `ExplicitCompletionBenchmarks.cs` SHA-256: `6b3deb808ebecf2c852033c51f26033903aeb924474e8ec32dbe3f81bb8f68cb`
- `BatchCompletionBenchmarks.cs` SHA-256: `6c7f3a3e1c594a01fcfe2c758e46fd9eab9fb4b0fb79eae198ee8a63d2110125`

Run each version sequentially, with no concurrent local builds/tests:

```bash
NuGetAudit=false dotnet run \
  --project tests/RequestBatcher.Benchmarks/RequestBatcher.Benchmarks.csproj \
  --configuration Release -- \
  --filter '*BatchCompletionBenchmarks.SharedBatchCompletion*' '*ExplicitCompletionBenchmarks*' \
  --artifacts /tmp/shared-completion-results
```

The focused repeat selects `SharedBatchCompletion`,
`ExplicitGroupPartialCancellationAsync`, and `ExplicitGroupsCapacityWaitAsync`.
The primitive uses one launch, three warmups, and eight measured iterations.
The public pipeline uses two launches, five warmups, twelve measured iterations,
and a 300 ms iteration target. The same arrays of 10/1,000 items and separate DI
providers per scenario are initialized outside measurement. All cases use the
public submission API, observe terminal outcomes, and validate per-ID counts.
Gate creation, a 1,000-entry counter reset, outcome observation, and validation
are included equally in both versions.

Standard cases use four keyed partitions, batch size 32, concurrency 8, and
capacity twice the group size. Cancellation routes to one of four partitions,
uses batch size/concurrency 1, cancels after the first handler starts, then
submits a sentinel to drain canceled queue entries before the next operation.
Capacity waiting uses one routed partition, concurrency 1, capacity equal to the
group size, a blocked single request, and two full groups. Mixed submissions
contain one group plus the same number of singles. An explicit group may span
many handler invocations; these are end-to-end operations, not per-item timings.

CSV and Markdown reports are in `before/`, `after/`, `before-repeat/`, and
`after-repeat/`. All 40 measured cases completed without validation failures.
Release build, pack, scoped formatting, and all 118 tests on each of net8.0 and
net10.0 passed. Existing deterministic tests cover failure fan-out, partial
cancellation, exactly-once completion/capacity release, and disposal/drain.
