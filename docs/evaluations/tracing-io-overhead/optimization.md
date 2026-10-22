# Tracing allocation optimization

This comparison measures the published 0.3.2 package against the optimized source snapshot using the same
[20 ms workload and SDK sampling matrix](README.md). It retains complete forward links at batch sampling,
reverse links for Activities requesting all data, original span lifetimes, and the existing completion bridge.

## Implementation boundary

All changes to tracing behavior and allocation policy stay under Diagnostics. Coordinator and response/delegate
adapters hold immutable metadata and call the tracing helper. No sampling flags or telemetry callbacks were added
to queue admission, backpressure, or request completion contracts.

- Call names and immutable creation-time tags are cached per coordinator/adapter.
- Propagation-only submission state does not allocate a reverse-link lock or collection.
- Dispatch only retains reverse-link recipients that request all Activity data. Forward links still include
  propagation-only contexts so an independently sampled batch can link to them.
- Repeated items from one submission do not allocate a deduplication hash set; the set is created only when
  a second distinct context appears. General multi-context deduplication is unchanged.

Record-only Activities still collect reverse links: the decision uses IsAllDataRequested rather than Recorded.
The completion bridge remains in Diagnostics to preserve aggregate exception objects, faulted cancellation,
cancellation tokens, and ending the span before completing the returned task.

## Results

Measured 2026-10-22 on Apple M2 Max, macOS 27.0.1, SDK 10.0.100, .NET 8.0.25, and BenchmarkDotNet 0.15.8.
Each case uses two launches, four warmups, twelve measured iterations, and four 1,024-item waves per iteration.
Values are per wave. KB is 1,024 bytes of managed allocations, not retained heap size. The published package runs
first; source is copied into an isolated tree before either run. Raw CSV files include 99.9% interval margins.

| Submission | Sampling | 0.3.2 allocated | Optimized allocated | Allocation reduction | 0.3.2 mean | Optimized mean |
| --- | --- | ---: | ---: | ---: | ---: | ---: |
| Singles | Disabled | 289.11 KB | 291.93 KB | -1.0% | 101.53 ms | 103.07 ms |
| Singles | 0% | 1,566.25 KB | 1,368.63 KB | 12.6% | 104.85 ms | 105.05 ms |
| Singles | 1% | 1,580.21 KB | 1,368.87 KB | 13.4% | 104.53 ms | 103.38 ms |
| Singles | 10% | 1,595.53 KB | 1,401.63 KB | 12.2% | 104.72 ms | 104.33 ms |
| Singles | 100% | 1,852.56 KB | 1,692.19 KB | 8.7% | 103.36 ms | 103.41 ms |
| Explicit groups | Disabled | 177.20 KB | 176.27 KB | 0.5% | 84.76 ms | 85.62 ms |
| Explicit groups | 0% | 260.37 KB | 247.02 KB | 5.1% | 84.84 ms | 86.02 ms |
| Explicit groups | 1% | 258.97 KB | 251.04 KB | 3.1% | 84.41 ms | 84.83 ms |
| Explicit groups | 10% | 260.60 KB | 246.73 KB | 5.3% | 84.80 ms | 85.35 ms |
| Explicit groups | 100% | 268.15 KB | 257.78 KB | 3.9% | 85.02 ms | 84.36 ms |

Corresponding time intervals overlap in every comparison. The experiment supports an allocation reduction, but
does not establish a consistent elapsed-time improvement or regression. Disabled allocation changes are small and
accompany variations in batch formation; no per-submission disabled tracing work was added. Metadata preparation
is outside the measured operation. There is a small fixed metadata cost when a coordinator/adapter is constructed.

The remaining allocation is substantial: optimized 0% singles still allocate about 4.7x the disabled path.
Propagation Activities, forward-link construction, submission context, and the completion bridge remain necessary
for the current contract. Lower sampling does not bypass them. This run changes several allocation sources together;
it does not isolate the cost of link operations from Activity and completion costs.

## Validation and limits

All 2,760 executed waves validated 2,826,240 request items exactly once, including warmup and diagnostic invocations.
Actual handler waits averaged 20.948–21.167 ms across launches. All sampled decisions matched completed sampled
spans; endpoint rates were exactly 0%/100%. Observed intermediate ratios varied with the finite number of traces.
Validation output records batch occupancy and actual sampling ratios for both implementations.

There is no exporter, real network/database call, ambient caller span, or concurrent local build/test during timing.
Fixed scenario order, timer scheduling, partially filled batches, and BenchmarkDotNet outlier handling limit small
time comparisons. These results describe finite waves, not steady-state GC pressure, peak memory, or individual
request P95/P99. Additional unit tests verify propagation-only lifetimes, mixed sampled/unsampled forward links,
record-only reverse links, duplicate contexts, and immutable sampler-visible tags.

Raw evidence: [0.3.2 CSV](package-optimization.csv), [optimized CSV](optimized.csv), and
[validation output](validation-optimized.txt). Validation lists the ten package cases followed by ten source cases,
each with two launches, in disabled/0%/1%/10%/100% order with singles then explicit groups.

## Reproduction

```bash
dotnet run --file scripts/CompareTracingIoOverhead.cs -- --compare-source /private/tmp/requestbatcher-tracing-optimized
```

The runner verifies the resolved package version and project source, snapshots both core and analyzer source without
build outputs, and requires ten successful CSV rows for each implementation. The measured snapshot and full logs
are retained locally under `/private/tmp/requestbatcher-tracing-optimized/run-7b66c25f45134f2a89869e2cce4c79ff`.
