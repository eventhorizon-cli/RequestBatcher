# Compact tracing context and links

## Contract and implementation

This evaluation isolates the compact submission context and shared batch-link collector from the earlier
[metadata and sampling optimization](optimization.md). The baseline is source commit
`c66f0c8225c98aacdea50c1991fad0432320da32`; both implementations use identical benchmark code and dependencies.

Propagation-only submissions carry only their ActivityContext. Recorded submissions keep the first handler context
inline, allocating a list only when additional batches process the same submission. One batch-link collector keeps
its first submission inline and uses a dictionary for additional distinct contexts; the same references supply
forward links and reverse-link delivery. The collector supports repeated enumeration by samplers.
No object pool, public API change, sampling change, or completion callback was added to queue logic.
The completion bridge remains unchanged after the separate [pending Task/ValueTask evaluation](completion-optimization.md).

## Results

Measured 2026-10-23 on Apple M2 Max, macOS 27.0.1 Arm64, SDK 10.0.100, .NET 8.0.25, and BenchmarkDotNet 0.15.8.
The workload matches the [original I/O comparison](README.md): 1,024 logical requests, 32-item batches, eight handler
slots, and a simulated 20 ms delay per handler call. SDK sampling uses ParentBasedSampler with a root ratio.
There is no exporter, network I/O, retained telemetry, or metrics provider.

Each case uses two launches, four warmups, twelve measured iterations, and four invocations per iteration.
Time and allocations include submission, completion, and exactly-once verification for the entire 1,024-item wave.
Errors below are BenchmarkDotNet 99.9% confidence interval margins; KB means 1,024 bytes, not retained memory.

| Submission | Sampling | Before mean ± error | After mean ± error | Before allocation | After allocation | Allocation change |
| --- | --- | ---: | ---: | ---: | ---: | ---: |
| 32 groups of 32 | Disabled | 84.50 ms ± 0.230 ms | 84.26 ms ± 0.195 ms | 174.98 KB | 175.16 KB | 0.10% |
| 32 groups of 32 | Sample0 | 84.39 ms ± 0.404 ms | 86.23 ms ± 2.004 ms | 244.93 KB | 243.09 KB | -0.75% |
| 32 groups of 32 | Sample1 | 84.47 ms ± 0.263 ms | 84.31 ms ± 0.300 ms | 245.04 KB | 240.68 KB | -1.78% |
| 32 groups of 32 | Sample10 | 84.48 ms ± 0.185 ms | 84.46 ms ± 0.243 ms | 245.54 KB | 240.72 KB | -1.96% |
| 32 groups of 32 | Sample100 | 84.52 ms ± 0.244 ms | 86.07 ms ± 2.170 ms | 257.3 KB | 245.96 KB | -4.41% |
| 1,024 singles | Disabled | 104.01 ms ± 2.190 ms | 103.40 ms ± 2.714 ms | 292.41 KB | 291.33 KB | -0.37% |
| 1,024 singles | Sample0 | 104.67 ms ± 2.162 ms | 105.62 ms ± 0.869 ms | 1368.02 KB | 1282.64 KB | -6.24% |
| 1,024 singles | Sample1 | 105.55 ms ± 0.905 ms | 105.57 ms ± 0.794 ms | 1371.54 KB | 1285.52 KB | -6.27% |
| 1,024 singles | Sample10 | 104.83 ms ± 1.869 ms | 105.33 ms ± 1.129 ms | 1403.19 KB | 1301.05 KB | -7.28% |
| 1,024 singles | Sample100 | 105.64 ms ± 0.297 ms | 105.75 ms ± 0.242 ms | 1691.6 KB | 1458.35 KB | -13.79% |

Single submissions allocate about 6.2% less at 0% sampling and 13.8% less at 100% sampling in this matched comparison.
Explicit groups already amortize call state across 32 requests; their measured reduction is smaller.
The disabled allocation differences are under 0.4%, and the disabled production path was not changed by this step.

All before/after time confidence intervals overlap. The group Sample0 and Sample100 cases include greater timer
variability in the second snapshot; their higher means do not establish a stable slowdown. Baseline then optimized
cases ran sequentially in fixed order, high-priority scheduling was unavailable, and no other local builds, tests,
or sample containers ran during measurement. Batch formation and runtime scheduling remain sources of variance.
These finite waves do not measure steady-state saturation, GC pauses, per-request tail latency, or exporter costs.

## Validation and reproduction

All 40 process launches completed, with 2,760 validated waves and 2,826,240 requests. Every wave checked each ID
exactly once, and completed sampled spans matched SDK sampling decisions. Both source trees were copied before
measurement and resolved as project references; the runner rejected missing, failed, or unexpected result rows.

Reports: [before CSV](links-before.csv), [after CSV](links-after.csv), and
[validation records](links-validation.txt). Validation records list the 20 baseline launches followed by the 20
optimized launches. Each snapshot runs singles then groups for disabled, 0%, 1%, 10%, and 100% sampling.

```bash
mkdir -p /private/tmp/requestbatcher-before-link-completion
git archive c66f0c8225c98aacdea50c1991fad0432320da32 | \
  tar -x -C /private/tmp/requestbatcher-before-link-completion
dotnet run --file scripts/CompareTracingIoOverhead.cs -- \
  --compare-source --baseline-source /private/tmp/requestbatcher-before-link-completion \
  /private/tmp/requestbatcher-tracing-links-final
```

The comparison runner's default package-baseline behavior is unchanged. `--baseline-source` requires
`--compare-source` and retains both source snapshots for review.

Local complete artifacts:
`/private/tmp/requestbatcher-tracing-links-final/run-274c3d0f5888456e8a4e3540e462633e`.
