# Sampling-aware links with 20 ms handler I/O

Skipping HandleBatch-to-Execute link collection for batches with no sampled submissions reduces managed allocation by **13.38% at 0%
sampling** and **9.69% at 1% sampling** for 1,024 single submissions. Explicit groups save 3.24–3.76% at 0–10%.
This run establishes an allocation benefit at low sampling rates, not a latency improvement. Full sampling allocates
0.40% more for singles and 1.06% more for groups, including the new submission-count attribute.

## Scope and behavior

The before snapshot already contains BufferQueue 2.1.0, direct read-only queue batches, compact tracing contexts,
and shared completion notifications. This comparison isolates the subsequent sampling-aware link changes and
submission-count attribute; it does not attribute earlier queue or Task improvements to this change.

- Execute collects Execute-to-HandleBatch links only when both `Recorded` and `IsAllDataRequested` are true. RecordOnly
  skips them.
- HandleBatch creates HandleBatch-to-Execute links only when at least one participating context is sampled. A mixed
  batch still links every distinct valid participating Execute context, including unsampled ones. It does not keep only
  sampled submissions.
- A recorded batch adds `requestbatcher.batch.submission.count`, counting distinct public submissions, not items or
  unique trace IDs. It reuses the link count when each item already has a distinct context; otherwise it counts shared
  completion identities after sampling. Dropped batches allocate no counting set.

Logs, public completion Tasks, outcome propagation, handler execution, and cancellation contracts are unchanged.
HandleBatch-to-Execute links are omitted for all-unsampled batches, so a custom sampler no longer receives those links. The
[observability design](../../observability-design.md) documents this correlation compatibility change.

The optional [`RequestBatcher.OpenTelemetry`](https://www.nuget.org/packages/RequestBatcher.OpenTelemetry) package moves the SDK
registration extensions out of core and adds the parameterless `AddRequestBatcherInstrumentation()` extension. Registering
the extension samples a `RequestBatcher.Handle` root whenever any link to a sampled Execute context is present,
and drops it otherwise. It improves trace completeness for sampled submissions; it is not the performance optimization
measured here. The benchmark registers `AddSource("RequestBatcher")` directly and leaves the application's sampler unchanged
on both sides, so it does not use this policy. Using the extension can increase recorded Handler spans relative to
independent root-ratio sampling, with corresponding recording/export costs.

## Method

The unchanged [C# benchmark](../../../tests/RequestBatcher.Benchmarks/InMemory/TracingIoOverheadBenchmarks.cs) submits
1,024 unique integer requests per wave, either as singles or 32 explicit groups of 32. Every handler awaits
`Task.Delay(20)`. BatchSize is 32, MaxConcurrency is 8, MaxPendingRequests is 1,024, and there is one queue partition.
Every wave verifies that each request ID was handled exactly once.

The real OpenTelemetry 1.20.1 SDK uses `ParentBasedSampler(new TraceIdRatioBasedSampler(ratio))` with ratios 0%, 1%,
10%, and 100%, plus a separate disabled-source case. There is no ambient caller, metric listener, exporter, or retained
telemetry. Execute and Handler roots therefore receive independent decisions in this experiment. A counting sampler
and processor verify that sampled decisions match completed sampled spans. A 0% provider still creates propagation
Activities and has substantially more allocation than leaving the source unregistered.

The [comparison runner](../../../scripts/CompareTracingIoOverhead.cs) snapshots each source and runs the published
0.2.4 package, then the before source, then the optimized source. The source snapshots both use BufferQueue 2.1.0.
BenchmarkDotNet 0.15.8 uses two launches, four warmups, twelve measured iterations, and four invocations per iteration.
Other local builds, tests, and Testcontainers sessions finished before measurement started.

Measured on 2026-10-24: macOS 27.0.1 Arm64, SDK 10.0.100, .NET 8.0.25, concurrent workstation GC. BenchmarkDotNet could
not identify the processor or enable high process priority. Values below cover an entire 1,024-item wave, including
submission, completion, and validation. KB means 1,024 bytes of managed allocation, not retained or peak heap size.
Time is milliseconds; error is BenchmarkDotNet's 99.9% confidence-interval margin.

## Results

| Submission | Sampling | Before mean ± error (ms) | After mean ± error (ms) | Before allocated (KB) | After allocated (KB) | Allocation change |
| --- | --- | ---: | ---: | ---: | ---: | ---: |
| 1,024 singles | Disabled | 99.92 ± 3.342 | 102.39 ± 1.890 | 283.70 | 284.53 | +0.29% |
| 1,024 singles | 0% | 104.28 ± 2.257 | 103.66 ± 2.220 | 1139.19 | 986.76 | -13.38% |
| 1,024 singles | 1% | 103.87 ± 2.189 | 104.55 ± 1.696 | 1140.35 | 1029.82 | -9.69% |
| 1,024 singles | 10% | 104.57 ± 1.723 | 102.76 ± 2.797 | 1155.90 | 1149.45 | -0.56% |
| 1,024 singles | 100% | 104.48 ± 2.242 | 103.88 ± 2.299 | 1313.24 | 1318.52 | +0.40% |
| 32 groups of 32 | Disabled | 85.55 ± 1.849 | 84.71 ± 0.377 | 168.96 | 169.62 | +0.39% |
| 32 groups of 32 | 0% | 84.66 ± 0.348 | 85.88 ± 1.857 | 229.64 | 221.71 | -3.45% |
| 32 groups of 32 | 1% | 85.74 ± 1.763 | 84.92 ± 0.541 | 230.41 | 221.74 | -3.76% |
| 32 groups of 32 | 10% | 84.67 ± 0.573 | 86.08 ± 2.356 | 230.56 | 223.09 | -3.24% |
| 32 groups of 32 | 100% | 84.63 ± 0.450 | 84.53 ± 0.401 | 235.59 | 238.09 | +1.06% |

Singles save 152.43 KB per wave at 0% and 110.53 KB at 1%. At 10%, most sufficiently large batches contain a sampled
submission, so retaining all mixed-batch contexts leaves little link allocation to remove. The measured 0.56% singles
difference is too small to establish useful savings. Explicit groups share one Execute/context across many items and
save only 7.47–8.67 KB per wave at low sampling rates.

At 100%, both the Execute and HandleBatch endpoints are always sampled and the optimization cannot skip their links.
The submission-count tag
adds recording work; measured net allocation increases by 5.28 KB for singles and 2.50 KB for groups. Batch formation
also varies between launches, so those deltas are not exact per-tag costs. The change is beneficial for low-sampling
workloads and does not establish an allocation improvement at full sampling. Disabled allocation differences are below
0.4%; its code path bypasses tracing, and these small differences do not establish a benefit or a fixed added cost.

Every matched before/after time confidence interval overlaps. Single-submission distributions also show multiple
modes, and the fixed run order cannot separate timer/scheduling drift from implementation cost. No general latency or
throughput speedup is established. Actual handler waits across launches averaged 20.887–21.097 ms.

The same campaign includes a fresh non-tracing legacy comparison:

| Submission | 0.2.4 mean ± error (ms) | Optimized, disabled mean ± error (ms) | 0.2.4 allocated (KB) | Optimized, disabled allocated (KB) |
| --- | ---: | ---: | ---: | ---: |
| 1,024 singles | 100.50 ± 2.763 | 102.39 ± 1.890 | 288.88 | 284.53 |
| 32 groups of 32 | 85.78 ± 2.228 | 84.71 ± 0.377 | 174.66 | 169.62 |

The legacy time intervals overlap the optimized disabled path. Its allocation differences include all intervening
library/dependency changes and must not be attributed to sampling-aware links alone.

All 22 cases and 44 launches completed: 3,036 validated waves and 3,108,864 requests, including warmup and diagnostic
invocations. Every ID was handled exactly once. Observed sampling was exactly 0% and 100% at the endpoints,
0.860–1.245% at 1%, and 9.669–10.194% at 10%. Completed sampled spans matched sampler decisions.

This finite-burst benchmark does not measure steady-state saturation, per-request tail latency, GC pauses, database
or exporter cost, or retained memory. It measures net8.0 only; correctness tests also cover net10.0. BenchmarkDotNet
removed some outliers under its default policy. Nothing in this result justifies pooling Activities or request contexts.

Raw evidence: [before CSV](sampled-links-before.csv), [after CSV](sampled-links-after.csv),
[0.2.4 CSV](sampled-links-legacy.csv), [validation records](sampled-links-validation.txt), and
[legacy package SHA-512](sampled-links-legacy.sha512).

## Correctness and reproduction

The complete Release solution builds with zero warnings/errors. All 437 unit tests pass on each of net8.0 and net10.0;
scoped formatting passes. Coverage includes both production assemblies. Six integration tests pass, including real
Testcontainers-backed HTTP, PostgreSQL, OTLP, Tempo, and Prometheus verification. The fixture removed its containers,
images, and network immediately afterward. SDK stress coverage includes mixed HandleBatch-to-Execute links, exactly-once
processing, failure, cancellation, shutdown, and integration-package sampling of Handler batch roots with a zero root ratio. Existing response unit tests verify
returned values, explicit input order, failure propagation, and cancellation; this change does not duplicate them in IT.

Both packages and symbol packages were packed locally with the same version and both framework assemblies.
Core's package metadata has no OpenTelemetry dependency; the integration package declares minimum versions of core
and OpenTelemetry without exact or upper constraints. Registration-extension users must install the integration package
and rebuild; direct source/meter registration remains available. No packages were published by this evaluation.

Use a source tree from before the sampling-aware link change as the baseline:

```bash
dotnet run --file scripts/CompareTracingIoOverhead.cs -- \
  --compare-source --include-legacy \
  --baseline-source /path/to/before-source-tree \
  /private/tmp/requestbatcher-sampled-links-final
```

The original before snapshot is `/private/tmp/requestbatcher-sampled-links-before`. The complete final run, including
both measured source trees, resolved dependencies, and full BenchmarkDotNet logs, is retained at
`/private/tmp/requestbatcher-sampled-links-final/run-97da03e7166c4a139dbd2ab5b5a46c9b`;
the combined log is `/private/tmp/requestbatcher-sampled-links-final.log`.
