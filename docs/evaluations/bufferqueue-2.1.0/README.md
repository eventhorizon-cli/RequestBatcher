# BufferQueue 2.1.0 with tracing and 20 ms downstream calls

The upgrade reduces managed allocation modestly in this workload: 2.66% for disabled single submissions, 4.55%
for disabled explicit groups, and 2.33–2.83% for explicit groups with tracing enabled. Traced singles vary from
0.03% to 1.23% less allocation; the smallest differences are not evidence of useful savings. The run does not
establish a consistent latency improvement from the queue upgrade.

## Scope and compatibility

This evaluation compares the same RequestBatcher source with BufferQueue 2.0.3 and 2.1.0. The baseline is
`82137a00cfc90ca42d35bf9ffe407fc661775ff3`; the updated source changes the dependency and passes the new
`IReadOnlyList<T>` batch directly into dispatch, removing the previous type-check/materialization fallback.
RequestBatcher's public API, handler input arrays, admission, cancellation, concurrency, and shutdown contracts
remain unchanged. Its net8.0 and net10.0 targets are unchanged.

Package metadata identifies these upstream sources:

- [BufferQueue 2.0.3](https://github.com/eventhorizon-cli/BufferQueue/tree/90c6208f940592f797ab8cf78ad4af88d1163649).
- [BufferQueue 2.1.0](https://github.com/eventhorizon-cli/BufferQueue/tree/c276547046223c1d62a13e0537b013622c775266).

BufferQueue 2.1.0 changes its pull API from `IAsyncEnumerable<IEnumerable<T>>` to
`IAsyncEnumerable<IReadOnlyList<T>>`. Returned batches remain valid across later pulls and commits. Both versions'
memory batches already support `IReadOnlyList<T>` at runtime, so removing RequestBatcher's cast does not remove
an existing array copy from the normal 2.0.3 path. The dependency also optimizes reader lookup, offsets, singleton
and sparse snapshots, and notification locking, and fixes producer lock handoff. Any measured difference includes
the whole dependency update; it cannot be attributed to the declared return type alone. Applications using
BufferQueue's changed interfaces directly must adapt and rebuild those implementations.

## Method

The unchanged [TracingIoOverheadBenchmarks](../../../tests/RequestBatcher.Benchmarks/InMemory/TracingIoOverheadBenchmarks.cs)
submits 1,024 unique requests per wave, either individually or as 32 explicit groups of 32. Each handler awaits
`Task.Delay(20)`. BatchSize is 32, MaxConcurrency is 8, MaxPendingRequests is 1,024, and there is one queue partition.
Each wave validates every request ID exactly once. Handler diagnostics report actual delay and batch formation;
the OpenTelemetry sampler and completed-span counts must agree.

Each version runs tracing disabled and SDK sampling at 0%, 1%, 10%, and 100%. The OpenTelemetry 1.20.1 SDK uses
`ParentBasedSampler` with root ratio sampling, a counting processor, and no ambient parent or exporter. Metrics
are disabled. No Docker services or telemetry backend are needed.

The existing [comparison runner](../../../scripts/CompareTracingIoOverhead.cs) takes isolated before/after source
snapshots and runs all ten cases per version sequentially. BenchmarkDotNet 0.15.8 uses two launches, four warmups,
twelve measured iterations, and four invocations per iteration. Other local builds and tests finish before the
campaign starts. Time and managed allocation cover the complete wave, including submission, completion, and
validation; these are neither per-request latency percentiles nor retained heap sizes.

## Results

Measured on 2026-10-24, macOS 27.0.1 Arm64, SDK 10.0.100, .NET 8.0.25, concurrent workstation GC. BenchmarkDotNet
could not identify the processor or enable high process priority in this environment. Both versions used the same
host and configuration. Resolved dependency lists differ only in BufferQueue's version.

Values are per 1,024-request wave. Time is milliseconds; allocated KB means 1,024 bytes. Error is BenchmarkDotNet's
99.9% confidence-interval margin. Negative allocation changes mean less allocation after upgrading.

| Submission | Sampling | 2.0.3 mean ± error (ms) | 2.1.0 mean ± error (ms) | 2.0.3 allocated (KB) | 2.1.0 allocated (KB) | Allocation change |
| --- | --- | ---: | ---: | ---: | ---: | ---: |
| 1,024 singles | Disabled | 105.92 ± 3.350 | 102.90 ± 2.531 | 290.76 | 283.04 | -2.66% |
| 1,024 singles | Sample0 | 106.75 ± 2.866 | 104.28 ± 2.609 | 1147.88 | 1136.36 | -1.00% |
| 1,024 singles | Sample1 | 100.35 ± 3.523 | 104.81 ± 2.588 | 1142.16 | 1140.63 | -0.13% |
| 1,024 singles | Sample10 | 103.53 ± 4.472 | 104.77 ± 1.980 | 1156.31 | 1156.01 | -0.03% |
| 1,024 singles | Sample100 | 103.96 ± 2.561 | 104.13 ± 2.636 | 1323.32 | 1307.09 | -1.23% |
| 32 groups of 32 | Disabled | 87.72 ± 1.278 | 86.06 ± 2.601 | 178.39 | 170.27 | -4.55% |
| 32 groups of 32 | Sample0 | 87.70 ± 0.681 | 84.82 ± 0.565 | 237.11 | 230.41 | -2.83% |
| 32 groups of 32 | Sample1 | 87.90 ± 0.804 | 85.04 ± 0.508 | 235.40 | 229.92 | -2.33% |
| 32 groups of 32 | Sample10 | 88.04 ± 2.083 | 85.13 ± 0.592 | 236.34 | 230.51 | -2.47% |
| 32 groups of 32 | Sample100 | 85.48 ± 0.899 | 85.58 ± 1.337 | 240.95 | 235.00 | -2.47% |

Disabled submissions allocate about 7.72 KB less per wave for singles and 8.12 KB less for explicit groups. With
tracing enabled, explicit groups save 5.48–6.70 KB; singles save 0.30–16.23 KB. Batch formation varies between
launches, especially for single submissions, so these are whole-wave observations rather than fixed per-item
savings. The update leaves the dominant Activity, tracing context, completion, and handler-input allocations in place.

All single-submission time intervals overlap between versions. Explicit groups at 0%, 1%, and 10% sampling are
about 3.3% faster in this run, but their actual handler waits are also shorter: about 21.29–21.78 ms before versus
21.00–21.06 ms after, despite both requesting a 20 ms delay. Roughly four batch rounds amplify that difference.
The versions run sequentially in a fixed order, so this result cannot separate dependency improvements from
timer and scheduling drift. At 100% sampling, both submission styles have nearly unchanged mean times. These
measurements do not establish a general throughput or latency speedup.

All 20 cases and 40 launches completed, validating 2,760 waves and 2,826,240 requests, including warmup and diagnostic
invocations. Every ID was handled exactly once. Across launches, actual mean handler waits ranged from 20.959–22.058 ms
before and 20.911–21.243 ms after. Observed sampling ranged from 0.657–1.051% at the 1% setting and 9.192–10.168%
at the 10% setting; endpoints were exactly 0% and 100%, and completed sampled spans matched sampler decisions.

This finite-burst experiment covers one partition, one batch size, and simulated I/O. It does not measure steady-state
saturation, per-request tail latency, database/exporter cost, CPU-bound handlers, or retained memory. The library's
net10.0 target passed unit tests, but the performance comparison measures net8.0 only. BenchmarkDotNet also warns
about a bimodal single-submission distribution and removes some outliers under its default policy.

Raw evidence: [before CSV](before.csv), [after CSV](after.csv), [validation records](validation.txt), and
[package SHA-512 checksums](package-sha512.txt). Full source snapshots and BenchmarkDotNet logs remain at
`/private/tmp/requestbatcher-bufferqueue-210-comparison/run-03ae07b3aecd4b7f9415f9f5236f985c`; the combined log is
`/private/tmp/requestbatcher-bufferqueue-210-comparison.log`.

## Integration validation

The Release solution build completed with zero warnings or errors. The scheduling/routing subset passed 28 tests
on each target framework, followed by all 418 unit tests on each of net8.0 and net10.0. Formatting of the three
changed C# files and Release package creation also passed. Test consumer implementations were adapted to the new
read-only batch interface, including failure after a started prefix and repeated disposal. No Docker services were
started for this upgrade evaluation.

## Reproduce

Prepare a baseline tree containing the pre-upgrade source, then run from the repository root:

```bash
mkdir -p /private/tmp/requestbatcher-bufferqueue-210-baseline
git archive 82137a00cfc90ca42d35bf9ffe407fc661775ff3 \
  global.json Directory.Build.props src/RequestBatcher src/RequestBatcher.Analyzers \
  | tar -x -C /private/tmp/requestbatcher-bufferqueue-210-baseline

dotnet run --file scripts/CompareTracingIoOverhead.cs -- \
  --compare-source --baseline-source /private/tmp/requestbatcher-bufferqueue-210-baseline \
  /private/tmp/requestbatcher-bufferqueue-210-comparison
```

The runner retains both source trees, resolved package assets, generated benchmark projects, and complete reports
in a unique `run-*` directory. Its measurement projects target .NET 8 independently of the runner SDK.
