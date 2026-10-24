# Tracing overhead with simulated downstream I/O

This evaluation compares published RequestBatcher 0.2.4 (before tracing instrumentation) with 0.3.2, using
a simulated 20 ms downstream operation per handler batch. Version 0.3.2 runs with no tracing provider and with
OpenTelemetry root sampling rates of 0%, 1%, 10%, and 100%.

The [allocation optimization comparison](optimization.md) separately measures 0.3.2 against the optimized source
with identical sampling modes, retaining all tracing and completion semantics.
The subsequent [compact-context and link comparison](compact-links.md) measures the next reduction against a source
snapshot of that implementation, without changing the Task completion bridge.
The [pending completion evaluation](completion-optimization.md) separately compares Task, ValueTask, and callback
bridges with every source still pending at registration.
The [shared completion comparison](shared-completion.md) measures the subsequent removal of tracing-only completion
sources on the ordinary admission path, including a fresh 0.2.4 baseline and the complete sampling matrix.
The [sampling-aware links comparison](sampled-links.md) measures the next reduction from skipping all-unsampled
HandleBatch-to-Execute links, including the submission-count attribute cost and a fresh non-tracing 0.2.4 baseline.

## Workload and sampling contract

Each wave submits 1,024 unique integer requests to one unkeyed queue partition, with BatchSize = 32,
MaxConcurrency = 8, and MaxPendingRequests = 1,024. A singleton handler awaits Task.Delay(20 ms) once per batch.
Two methods submit either 1,024 individual requests or 32 explicit groups of 32. Every wave verifies that each
ID was handled exactly once. There is no Task.Run, network service, metric listener, exporter, or retained telemetry.

Sampling uses the real OpenTelemetry 1.20.1 SDK and
`ParentBasedSampler(new TraceIdRatioBasedSampler(rate))`. A counting sampler delegates every decision to this
sampler; a minimal processor counts completed sampled spans. Neither retains Activities. The benchmark validates
that completed sampled spans match the sampling decisions, including all-drop and all-record endpoints.
The 1% and 10% cases must observe both sampled and dropped spans.

There is no ambient caller Activity: Execute and independent HandleBatch roots are sampled separately.
HandleBatch-to-Execute and Execute-to-HandleBatch links do not force matching sampling decisions. In an application with parent traces, parent-based decisions
can produce different effective rates. A 0% SDK sampler is distinct from removing the provider: it preserves
propagation context and still exercises instrumentation.

## Results

Measured 2026-10-22 on Apple M2 Max / macOS 27.0.1 Arm64, SDK 10.0.100, .NET 8.0.25,
concurrent workstation GC, and BenchmarkDotNet 0.15.8. Both versions are NuGet packages, not local builds.

Time and managed allocations are per complete 1,024-item wave, including submission and validation.
They are not individual-request latency percentiles, peak memory, or retained heap size. Error is the
BenchmarkDotNet 99.9% confidence interval margin; KB means 1,024 bytes.

| Submission | Package / sampling | Mean | Error | Allocated |
| --- | --- | ---: | ---: | ---: |
| 1,024 singles | 0.2.4, no instrumentation | 101.32 ms | 3.728 ms | 289.46 KB |
| 1,024 singles | 0.3.2, disabled | 103.24 ms | 3.117 ms | 290.51 KB |
| 1,024 singles | 0.3.2, 0% | 103.61 ms | 2.260 ms | 1,569.04 KB |
| 1,024 singles | 0.3.2, 1% | 104.43 ms | 2.298 ms | 1,568.27 KB |
| 1,024 singles | 0.3.2, 10% | 103.61 ms | 3.205 ms | 1,590.05 KB |
| 1,024 singles | 0.3.2, 100% | 104.05 ms | 2.191 ms | 1,848.11 KB |
| 32 groups of 32 | 0.2.4, no instrumentation | 85.11 ms | 0.798 ms | 174.34 KB |
| 32 groups of 32 | 0.3.2, disabled | 84.32 ms | 0.270 ms | 176.35 KB |
| 32 groups of 32 | 0.3.2, 0% | 84.68 ms | 0.602 ms | 259.63 KB |
| 32 groups of 32 | 0.3.2, 1% | 85.20 ms | 0.733 ms | 259.83 KB |
| 32 groups of 32 | 0.3.2, 10% | 85.04 ms | 1.273 ms | 261.57 KB |
| 32 groups of 32 | 0.3.2, 100% | 84.76 ms | 0.318 ms | 274.52 KB |

The disabled 0.3.2 and 0.2.4 confidence intervals overlap: this run establishes neither a speedup nor a regression.
Sampling rates show no monotonic elapsed-time trend. Their confidence intervals overlap, so the small differences
should not be interpreted as stable latency overhead bounds. Single-item throughput derived from wave means is
approximately 9,800–9,900 items/s for 0.3.2; explicit groups yield approximately 12,000–12,100 items/s.
These rates count logical items, not handler calls or a steady-state capacity limit.

Allocations are more sensitive. Reducing sampling from 100% to 1% lowers single-submission allocation by about
15.1%, and explicit-group allocation by about 5.4%. At 0%, allocation remains 5.40x disabled for singles and
1.47x disabled for groups. Lower sampling therefore does not make the built-in instrumentation allocation cost
proportional to the percentage of traces recorded.

The measured [0.3.2 tracing implementation](https://github.com/eventhorizon-cli/RequestBatcher/blob/v0.3.2/src/RequestBatcher/Diagnostics/RequestBatcherTracing.cs) creates
submission context and completion wrappers whenever a propagation Activity exists, and collects
HandleBatch-to-Execute links from every participating submission before the sampling decision. Sampled Execute spans
additionally collect Execute-to-HandleBatch links to the batches that processed their requests. This explains
why much of the allocation remains at 0%. Explicit submission amortizes Execute tracing across 32 items.
The tiny 0%/1% allocation inversion for singles is not evidence that sampling saves more at 1%: batch formation
varies across launches.

Async waiting dominates elapsed time here. Higher sustained traffic could turn additional allocations into GC
pressure, but this finite-wave experiment does not measure that effect. Sampling can also reduce exporter,
transport, and backend work; those costs are outside this benchmark.

## Validation and uncertainty

All 1,656 executed waves, including warmup and diagnostic invocations, verified every ID exactly once:
1,695,744 items total. Actual handler waits averaged 20.934–21.296 ms across the process launches.
Observed sampling ratios were 0% and 100% exactly at the endpoints, 0.860–1.031% at the 1% setting,
and 9.398–10.530% at the 10% setting. All sampled decisions matched completed sampled spans.

Single-submission runs averaged 35.464–37.725 handler calls per wave, versus 32.000–32.072 for explicit groups.
Admission overlaps dispatch, so early batches can be partially filled. With eight handler slots, 32 full batches
need four rounds of downstream waits; additional batches can require a fifth round. Timing comparisons therefore
include changes in batch formation, not just the cost of Activity creation. Explicit submission does not guarantee
one handler call; near-full batches are a result of this workload.

Each case uses two launches, four warmups, twelve measured iterations, and four waves per iteration.
Input preparation, DI setup, provider setup, and shutdown are outside measurement; submission, completion,
exactly-once validation, sampling counters, and handler timing counters are included consistently.
The processor's counter is part of the recorded path. Aggregate validation counters include non-measured
invocations and must not replace BenchmarkDotNet summary statistics.

Versions ran sequentially (0.2.4 first), and current sampling cases ran in fixed order. No concurrent local build
or test was started during the measurement. High-priority scheduling was unavailable; BenchmarkDotNet reported a
multimodal single-submission distribution and removed outliers using its defaults. Timer variability and fixed
ordering limit small comparisons. No per-request P95/P99, actual HTTP/database overhead, or exporter cost was measured.

Raw reports: [baseline.csv](baseline.csv), [current.csv](current.csv), and [validation.txt](validation.txt).
Validation rows have two launches per case: old singles/groups, then current singles/groups for disabled,
0%, 1%, 10%, and 100%, in that order.

## Reproduction

Source: [TracingIoOverheadBenchmarks.cs](../../../tests/RequestBatcher.Benchmarks/InMemory/TracingIoOverheadBenchmarks.cs).
Runner: [CompareTracingIoOverhead.cs](../../../scripts/CompareTracingIoOverhead.cs).

```bash
dotnet run --file scripts/CompareTracingIoOverhead.cs -- /private/tmp/requestbatcher-tracing-sampling
```

The runner builds two isolated projects with identical benchmark source and dependencies, verifies package versions
in project.assets.json, and retains SHA-512 package evidence and complete BenchmarkDotNet reports. It rejects
missing/failed reports and unexpected case counts (two baseline and ten current rows). It does not alter repository
package references. Use --help for usage; invalid arguments return exit code 2.

```text
0.2.4  16784b16f17d1af2315161d0df1e81aedfb0a56bd1028fba49b5493050bed3a78f9da0681862bb8c1c3a0557fabe5c6943309f9e31ede8b96329b1f76099da32
0.3.2  de3d4baf3058fbaf82e3041f8352d6c154fb244d0130d0bb89472b910a0e8a5abdac171629cd853bd803f8bce3a8236aa9c0c6ccbc6f2ea4bbce4714a9ed4977
```

Complete local artifacts:
`/private/tmp/requestbatcher-tracing-sampling/run-4e9a981e98114b988c4bd60068e4d485`.
