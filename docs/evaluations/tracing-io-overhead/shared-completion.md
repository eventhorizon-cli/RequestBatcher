# Shared tracing completion with 20 ms downstream I/O

## Contract and implementation

This evaluation compares the published RequestBatcher 0.2.4 package, which has no tracing instrumentation, with
matched source snapshots before and after sharing request completion with tracing. The before snapshot was taken
from `main` at `11c36c2591143052a1b8d664816fa775f8a8f220` with the pending short telemetry-type-name changes already
applied. Both source snapshots therefore use the same span attributes. Public Task APIs, handler concurrency,
batch formation, cancellation, response semantics, and the relationships represented by HandleBatch-to-Execute and
Execute-to-HandleBatch links are unchanged.

Observed request, typed-response, and aggregate completion sources use their existing caller-visible Task. They
prepare the outcome and notify Diagnostics before publishing it. Diagnostics captures the submission execution
context, collects Execute-to-HandleBatch links to the batches that processed the submission, stops the Execute Activity,
and then releases that same completion source. A small
publication gate handles completion racing with the synchronous submission boundary. Ordinary completion objects
have no additional fields when tracing is disabled, and no completion source is pooled.

This removes the tracing-only TaskCompletionSource and async waiter when admission returns the completion Task
directly. Capacity-wait paths can return a producer wrapper, and response-group submissions still materialize their
result array through an async adapter. Those paths retain the tracing bridge so span duration includes the entire
operation. This evaluation does not claim that every submission path now allocates only one Task.

Completion notifications run on the completing thread under the captured context, then restore that thread's
context. Expensive synchronous telemetry processors can therefore consume completion/dispatch time. The benchmark
uses a counting processor with no exporter; it does not measure exporter or network costs.

## Workload

The unchanged [benchmark](../../../tests/RequestBatcher.Benchmarks/InMemory/TracingIoOverheadBenchmarks.cs) submits
1,024 unique requests per wave, individually or as 32 explicit groups of 32. Each handler call awaits Task.Delay(20 ms).
BatchSize is 32, MaxConcurrency is 8, MaxPendingRequests is 1,024, and there is one queue partition. The dispatcher
reserves one execution slot before pulling one batch; this change adds no multi-batch prefetching.

The OpenTelemetry 1.20.1 SDK uses ParentBasedSampler with root ratios of 0%, 1%, 10%, or 100%, or no provider at all.
There is no ambient parent, metrics provider, exporter, external service, or retained telemetry. The old package runs
only the two disabled cases. Each source snapshot runs all ten cases. Every wave checks every request ID exactly once;
sampled spans must complete in the counts selected by the SDK sampler.

BenchmarkDotNet uses two launches, four warmups, twelve measured iterations, and four invocations per iteration.
Mean time and managed allocations cover the entire wave, including submission, completion, and validation. They are
not per-request latency percentiles or retained heap sizes. KB denotes 1,024 bytes, and error is the BenchmarkDotNet
99.9% confidence-interval margin. All three versions run sequentially, with no concurrent local build or test jobs.

## Results

Measured on 2026-10-23 with Apple M2 Max, macOS 27.0.1 Arm64, SDK 10.0.100, .NET 8.0.25, workstation concurrent GC,
and BenchmarkDotNet 0.15.8. Values are per 1,024-request wave.

| Submission | Sampling | Before mean ± error | After mean ± error | Before allocated | After allocated | Allocation change |
| --- | --- | ---: | ---: | ---: | ---: | ---: |
| 1,024 singles | Disabled | 102.16 ± 2.381 ms | 103.46 ± 2.417 ms | 289.01 KB | 293.09 KB | +1.41% |
| 1,024 singles | 0% | 105.49 ± 0.796 ms | 105.22 ± 1.204 ms | 1,284.93 KB | 1,149.63 KB | -10.53% |
| 1,024 singles | 1% | 104.91 ± 1.592 ms | 105.28 ± 1.205 ms | 1,285.78 KB | 1,147.56 KB | -10.75% |
| 1,024 singles | 10% | 105.53 ± 1.184 ms | 105.88 ± 0.401 ms | 1,300.79 KB | 1,162.29 KB | -10.65% |
| 1,024 singles | 100% | 105.54 ± 0.943 ms | 105.06 ± 0.937 ms | 1,459.60 KB | 1,321.68 KB | -9.45% |
| 32 groups of 32 | Disabled | 84.46 ± 0.308 ms | 85.54 ± 1.757 ms | 174.72 KB | 175.13 KB | +0.23% |
| 32 groups of 32 | 0% | 85.52 ± 1.754 ms | 84.51 ± 0.307 ms | 240.75 KB | 235.54 KB | -2.16% |
| 32 groups of 32 | 1% | 85.13 ± 1.472 ms | 84.32 ± 0.384 ms | 243.06 KB | 235.53 KB | -3.10% |
| 32 groups of 32 | 10% | 84.54 ± 0.332 ms | 84.23 ± 0.139 ms | 240.79 KB | 235.82 KB | -2.06% |
| 32 groups of 32 | 100% | 84.97 ± 1.025 ms | 84.62 ± 0.310 ms | 249.66 KB | 240.83 KB | -3.54% |

The shared-completion change saves about 135–139 KB per wave of individually submitted requests with tracing enabled.
Explicit groups amortize completion state over 32 items, so their reduction is smaller. All matched before/after time
confidence intervals overlap; this experiment establishes an allocation reduction, not a stable latency improvement.
The disabled singles allocation difference is 1.41%, despite the ordinary completion sources retaining their fields;
batch formation and runtime scheduling vary between launches. This is not a claim of zero disabled-path overhead.

The legacy comparison uses the same run and workload:

| Submission | 0.2.4 mean ± error | Optimized, disabled mean ± error | 0.2.4 allocated | Optimized, disabled allocated |
| --- | ---: | ---: | ---: | ---: |
| 1,024 singles | 103.63 ± 2.081 ms | 103.46 ± 2.417 ms | 289.26 KB | 293.09 KB |
| 32 groups of 32 | 84.39 ± 0.228 ms | 85.54 ± 1.757 ms | 173.64 KB | 175.13 KB |

With tracing disabled, both time intervals overlap the legacy package; measured allocation differs by approximately
1.3% for singles and 0.9% for groups. Within the optimized version, tracing still allocates about 3.9 times the disabled
amount at 0% sampling and 4.5 times at 100% for single submissions. Activities, propagation context, tags, and
the HandleBatch-to-Execute and Execute-to-HandleBatch links remain. Reducing sampling therefore does not make
instrumentation allocation proportional to the recording rate.
The slight allocation inversion between 0% and 1% does not establish an advantage for 1% sampling.

All 22 cases and 44 process launches completed: 3,036 validated waves and 3,108,864 requests. Each wave verified every
ID exactly once. Actual handler delay averaged 20.947–21.320 ms across launches. Observed sampling was exactly 0% and
100% at the endpoints, 0.860–1.076% at the 1% setting, and 9.873–10.938% at the 10% setting. Completed sampled spans
matched sampler decisions.

These finite waves include partially filled early batches and timer/scheduling variability. They do not measure
steady-state saturation, per-request tail latency, GC pause duration, response-group performance, sustained capacity
waiting, or exporter cost. Versions and sampling cases run in fixed order, and high-priority scheduling was unavailable.

Raw reports: [0.2.4 CSV](shared-completion-legacy.csv), [before CSV](shared-completion-before.csv),
[after CSV](shared-completion-after.csv), and [validation records](shared-completion-validation.txt).

## Allocation investigation

The first candidate did not measurably reduce allocations: at 0% sampling, 1,024 singles changed from 1,284.47 KB
to 1,279.92 KB, and at 100% from 1,459.20 KB to 1,457.77 KB. These differences are too small to establish useful savings.
The [initial CSV](shared-completion-initial-after.csv) and [validation](shared-completion-initial-validation.txt)
are retained separately from the final results.

A bounded EventPipe allocation trace showed that the shared path was active: sampled allocations included
TracedRequestCompletion and CallCompletionObserver, without a sampled CompleteCallAsync state-machine allocation.
A separate instrumented source copy counted 10,240 direct attachments and zero fallback attachments for each of
0% and 100% sampling after warmup. The dominant additional string stack was:

```text
System.String.Ctor
  <- Activity.get_Id
  <- ExecutionContext.RunInternal
  <- CallCompletionObserver.Publish
```

System.Diagnostics.DiagnosticSource 10.0.11 records source commit `e2f47b0110ed922f21a1522da67279133ce28f32`.
Its [Activity.Current setter validation](https://github.com/dotnet/dotnet/blob/e2f47b0110ed922f21a1522da67279133ce28f32/src/runtime/src/libraries/System.Diagnostics.DiagnosticSource/src/System/Diagnostics/Activity.cs#L1409)
reads Activity.Id, whose [getter](https://github.com/dotnet/dotnet/blob/e2f47b0110ed922f21a1522da67279133ce28f32/src/runtime/src/libraries/System.Diagnostics.DiagnosticSource/src/System/Diagnostics/Activity.cs#L234)
lazily creates the combined W3C ID string. Assigning the already-current Activity forced this otherwise unused
55-character string for every request. It is cached per Activity; this is not a copy of the Activity or a new string
on every subsequent assignment. On the measured 64-bit runtime, one such string occupies about 136 bytes.

The final implementation compares references before assigning Activity.Current. Restoring the captured execution
context already supplies the correct Activity in the ordinary path, so the redundant assignment and ID materialization
are avoided. Explicit restoration remains available when execution-context flow was suppressed.

The [sampled allocation types](shared-completion-initial-allocation-types.csv),
[trace summary](shared-completion-initial-allocation-summary.txt), and
[probe validation](shared-completion-initial-allocation-validation.txt) retain the diagnostic evidence.
Sampled type weights are estimates, not exact object counts. The raw trace and full allocation stacks are local at
`/private/tmp/requestbatcher-shared-sample0.nettrace` and `/private/tmp/requestbatcher-shared-sample0.stacks.csv`.

## Correctness and reproduction

The final implementation passes 409 unit tests on each of net8.0 and net10.0, including tests for original Task
identity, completion before/after attachment, span completion before Task publication, captured AsyncLocal/Activity
restoration, fallback publication, typed results, faulted OperationCanceledException, cancellation tokens, and full
aggregate exceptions. The complete Release solution builds without warnings, and scoped formatting passes.

Three in-process integration stress cases use the real SDK, including 32 waves of 512 requests at each of 0% and
100% sampling, with capacity limited to 64. They verify exactly-once processing, actual batch membership in both
HandleBatch-to-Execute and Execute-to-HandleBatch links, cancellation, failure, and shutdown. Unit and integration
coverage were collected separately.
These tests do not start Docker or run the container-backed sample integration cases.

The [comparison runner](../../../scripts/CompareTracingIoOverhead.cs) retains both source snapshots, validates the
resolved legacy package and its hash, and rejects missing, failed, or unexpected result rows. Reproduce from the
repository root with the source tree to compare supplied explicitly:

```bash
dotnet run --file scripts/CompareTracingIoOverhead.cs -- \
  --compare-source --include-legacy \
  --baseline-source /path/to/before-source-tree \
  /private/tmp/requestbatcher-shared-completion-comparison
```

`--include-legacy` requires `--compare-source`; existing runner modes remain available. The original local before
snapshot is `/private/tmp/requestbatcher-shared-completion-before`, with its file hashes in `SNAPSHOT.sha256`.

Complete final artifacts, including both measured source snapshots and the legacy package hash, are retained at
`/private/tmp/requestbatcher-shared-completion-final/run-41c3dc3df8e7473d9bc614ccc7cef8c1`.
