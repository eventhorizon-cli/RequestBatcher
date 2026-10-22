# Shared explicit completion source

Deriving the shared completion directly from `TaskCompletionSource` removes one object
per explicit submission: **168 B → 144 B, saving 24 B per group**, independent of item count.
It still waits for all items, preserves terminal outcomes and uses asynchronous continuations.
No caller task or pending state is pooled.

## Results

Primitive measurements use 128 / 1,024 / 8,192 completion notifications:

| Campaign | Before times | After times | Allocated before / after |
|---|---|---|---|
| Initial, before then after | 485.9 / 3,717.9 / 29,621.1 ns | 503.1 / 3,833.0 / 30,644.5 ns | 168 / 144 B |
| Focused repeat, after then before | 510.0 / 3,844.5 / 30,389.8 ns | 492.1 / 3,791.5 / 30,026.9 ns | 168 / 144 B |

The allocation reduction reproduced, while timing changed direction between campaigns.
Selected pipeline results retain the main timing concerns:

| Scenario / campaign | Items/group | Before | After | Before allocated | After allocated |
| --- | ---: | ---: | ---: | ---: | ---: |
| Success / initial | 10 | 8.635 μs | 8.379 μs | 5.11 KB | 5.09 KB |
| Success / initial | 1,000 | 162.655 μs | 161.394 μs | 181.58 KB | 181.56 KB |
| Partial cancellation / initial | 1,000 | 562.499 μs | 634.822 μs | 472.01 KB | 471.98 KB |
| Partial cancellation / reverse-order repeat | 1,000 | 557.53 μs | 582.94 μs | 472.00 KB | 471.99 KB |
| Capacity wait / initial | 1,000 | 331.636 μs | 348.933 μs | 365.95 KB | 365.93 KB |
| Capacity wait / reverse-order repeat | 1,000 | 337.09 μs | 325.95 μs | 365.95 KB | 365.91 KB |

**No throughput improvement is claimed.** Large partial cancellation initially slowed 12.86%;
the repeat slowed 4.56%, with overlapping error ranges (557.53 ± 5.196 vs 582.94 ± 36.261 μs).
Capacity-wait timing changed direction. Short-iteration and multimodal warnings prevent a
stable latency conclusion; a small regression is not ruled out. KB means 1,024 bytes.

## Method and reproduction

Measured 2026-10-13, Arm64 macOS, SDK 10.0.100, .NET 8.0.25, BenchmarkDotNet 0.15.8.
Baseline `b93849a`, candidate `1fe5abd`, identical benchmark sources.
Primitive: one launch, three warmups, eight measurements. Pipeline: two launches, five
warmups, 12 measurements, 300 ms target. All 40 measured cases validated successfully.

Input arrays and DI providers are outside measurement. Gates, counter resets, admission,
outcome observation and per-ID validation are included equally. Normal cases use four keyed
partitions, batch size 32, concurrency 8 and capacity twice the group size (10/1,000).
Cancellation uses batch size/concurrency 1 and a drain sentinel. Capacity tests block a single
request before admitting two full groups; mixed tests combine one group and the same number
of singles. These are whole-pipeline waves, not per-item latency or isolated object sizes.

```bash
dotnet run --project tests/RequestBatcher.Benchmarks/RequestBatcher.Benchmarks.csproj \
  --configuration Release -- \
  --filter '*BatchCompletionBenchmarks.SharedBatchCompletion*' '*ExplicitCompletionBenchmarks*' --artifacts /private/tmp/shared-completion-results
```

## Observability

The recorded completion-source comparison does not include the built-in RequestBatcher instrumentation.
See the [observability design](../../observability-design.md) for call duration and batch correlation.
