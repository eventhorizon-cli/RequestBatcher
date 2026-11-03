# Response and tracing hot-path overhead

This evaluation compares response materialization, assignment, and tracing against
`14310ba11f00eb0cd557a5686e6e483182ad040d` on .NET 10. It also evaluates whether the
response factory's temporary array should use `ArrayPool<T>`.

The candidate shares one response-entry snapshot between explicit admission and
ordered result assembly and assigns the factory's owned response array directly.
Response entries have one writer and use a value plus a Boolean flag; handler and
request completion provide the publication boundaries. Assignment checks each
entry once, and post-handler validation uses a dedicated `EnsureResponseIsSet`
method before publishing any success. Response loops cache stable item counts.
Tracing caches named batch span names, traverses indexable snapshots by index,
and skips redundant `Activity.Current` assignments. Public API signatures,
response order, cancellation, all-or-nothing admission, and handler scope
boundaries retain their contracts. Duplicate assignment still throws and preserves
the existing response, but earlier assignments in that helper call now remain.
Handlers must await all response-producing work before returning. These ownership
and error semantics are documented in both READMEs and the response design.
The completion observer also restores the full completion-thread context when
submission suppresses flow, including `AsyncLocal` values written by span listeners.

## Environment and comparison method

The measurements run on an Apple M2 Max with macOS 27.2 (26B123), SDK 10.0.100,
BenchmarkDotNet 0.15.8, and .NET 10.0.0 (10.0.25.52411), Arm64 RyuJIT and concurrent
workstation GC. Every benchmark executable targets `net10.0`. No Docker containers
or external services are used.

The baseline is an isolated archive of the commit above with the new benchmark
workloads copied in. The candidate uses the same committed tree and benchmark
workloads with the changed production files substituted. The runs are sequential;
no builds or tests run alongside measured iterations. Setup, dependency injection,
initial warmup, and provider disposal are outside measurement. Submission,
completion, handler accounting, and correctness validation are measured equally.
BenchmarkDotNet cannot raise process priority on this machine; both versions use
the same normal priority.

The initial 34-case matrix uses the
[matrix-source SHA-256](source-sha256.txt). Response submission and assignment
were then measured again after the single-writer and dedicated-validation changes;
their final tables use that 12-case run and the
[final-source SHA-256](final-source-sha256.txt). The named-tracing and SDK tables
retain the earlier measurements: both workloads are request-only and their
execution paths are unchanged by the response-entry revision. Explanatory tracing
comments changed after the matrix was frozen.
The four main benchmark workload hashes match across baseline, matrix candidate,
and final candidate. Earlier response results are retained as
[submission](before-single-writer-response.csv) and
[assignment](before-single-writer-assignment.csv) diagnostics.
The pre-count-cache diagnostic has a separate
[source manifest](before-count-source-sha256.txt). Pooling and sequence-disassembly
results below are historical experiments with the earlier response-slot logic;
their timings do not describe the final single-writer implementation.

Time and allocation are **per wave or batch**, as identified by each table, rather
than per request. `±` denotes the exported 99.9% confidence-interval margin. Changes
use `(after / before - 1) × 100`; negative values mean less time or allocation.
Point estimates alone do not establish a timing improvement when intervals overlap.
CSV ratio columns compare methods within a run, rather than source versions.
Narrow within-run intervals do not account for every difference between separate
runs; the repeated sequence measurements below demonstrate that limitation.

## Response submission

[ResponseHotPathBenchmarks](../../../tests/RequestBatcher.Benchmarks/InMemory/ResponseHotPathBenchmarks.cs)
submits 256 distinct integer requests per wave, either as concurrent single calls
or one explicit submission. Typed singleton and service-free static-delegate
handlers share the same response implementation. Batch limit is 32, concurrency
is eight, capacity is 256, and tracing/metrics are disabled. Handlers complete
synchronously or await a 20 ms simulated operation. Each wave validates every
response value, explicit response order, exactly-once handling, batch size, and
concurrency.

The job uses two launches, three warmups, eight measurement iterations, and four
waves per iteration. Synchronous iterations are short; their time estimates need
to be read with their reported variation.

| Handler | Submission | Delay (ms) | Before (µs/wave) | After (µs/wave) | Time change | Before → after (KB/wave) |
| --- | --- | ---: | ---: | ---: | ---: | ---: |
| Typed | Explicit | 0 | 136.5 ± 9.10 | 118.2 ± 12.31 | -13.4% | 53.06 → 47.36 |
| Typed | Singles | 0 | 141.3 ± 19.65 | 128.0 ± 25.73 | -9.4% | 74.88 → 73.38 |
| Delegate | Explicit | 0 | 137.9 ± 9.00 | 114.7 ± 12.85 | -16.8% | 53.06 → 47.36 |
| Delegate | Singles | 0 | 146.5 ± 18.55 | 122.9 ± 19.97 | -16.1% | 75.09 → 73.16 |
| Typed | Explicit | 20 | 22,182.9 ± 325.34 | 21,517.1 ± 458.71 | -3.0% | 59.18 → 53.73 |
| Typed | Singles | 20 | 42,837.7 ± 2,228.86 | 38,612.9 ± 4,769.50 | -9.9% | 90.35 → 90.19 |
| Delegate | Explicit | 20 | 22,194.7 ± 366.50 | 21,414.0 ± 307.25 | -3.5% | 59.24 → 53.79 |
| Delegate | Singles | 20 | 42,030.8 ± 2,896.00 | 40,602.6 ± 2,633.70 | -3.4% | 90.21 → 87.96 |

Explicit submissions allocate 10.7% less with synchronous handlers and about 9.2%
less with delayed handlers. Single-call allocation changes are smaller and depend
on batch formation. Several synchronous intervals overlap the baseline; these
short iterations do not establish a general CPU improvement.
Delayed waves also include timer scheduling and potentially partial batches.

Raw data: [before](before-response.csv), [after](after-response.csv).

## Response assignment

[ResponseAssignmentBenchmarks](../../../tests/RequestBatcher.Benchmarks/InMemory/ResponseAssignmentBenchmarks.cs)
compares the public factory and ordered-sequence overloads at 32/256 entries.
Both construct fresh entries and validate every response inside measurement.
The ordered control also constructs its response input there. The job uses two
launches, three warmups, ten measurement iterations, and automatic calibration.
Results are per batch.

| Overload | Count | Before (ns/batch) | After (ns/batch) | Time change | Before → after (KB/batch) |
| --- | ---: | ---: | ---: | ---: | ---: |
| Ordered sequence | 32 | 279.9 ± 0.46 | 247.3 ± 0.80 | -11.6% | 1.57 → 1.57 |
| Factory | 32 | 281.8 ± 0.83 | 236.9 ± 0.53 | -15.9% | 1.57 → 1.42 |
| Ordered sequence | 256 | 2,197.6 ± 5.94 | 1,889.3 ± 6.10 | -14.0% | 12.07 → 12.07 |
| Factory | 256 | 2,193.2 ± 5.86 | 1,911.3 ± 6.85 | -12.9% | 12.07 → 11.05 |

The factory overload removes one response-array copy: allocation falls from 1.57
to 1.42 KB at 32 entries and from 12.07 to 11.05 KB at 256 entries. All four final
assignment cases use 11.6–15.9% less time than the baseline, with non-overlapping
exported intervals. These are combined results for removing slot atomics and the
unset prepass, keeping one assignment loop, and avoiding the factory's array copy;
the experiment does not isolate each contribution.

The earlier shared-helper implementation regressed ordered assignment by
2.9–3.6%. The final source removes that helper. Duplicate assignment is checked
only by `SetResponse`, and handler adapters use `EnsureResponseIsSet` without
reading and discarding each response value. A missing response still fails the
whole handler batch before any request success is published. Factory failures
and count mismatch remain before helper writes; a factory-side count change
throws `InvalidOperationException` with an operation-specific message.

Raw data: [before](before-assignment.csv), [after](after-assignment.csv).

Adding `AggressiveInlining` to the earlier shared helper was tested separately:

| Overload | Count | Without hint (ns/batch) | With hint (ns/batch) |
| --- | ---: | ---: | ---: |
| Ordered sequence | 32 | 289.3 ± 1.46 | 288.9 ± 2.79 |
| Factory | 32 | 278.4 ± 2.76 | 281.0 ± 5.80 |
| Ordered sequence | 256 | 2,250.7 ± 29.57 | 2,279.2 ± 26.46 |
| Factory | 256 | 2,193.6 ± 16.57 | 2,245.4 ± 36.10 |

Intervals overlap in all four cases and allocations are unchanged. The hint
demonstrated no timing improvement and is not present in production. Both source
variants completed four cases and eight validated process launches. Raw data:
[control](inline-control.csv), [hint](inline-candidate.csv),
[control validation](inline-control-validation.txt),
[hint validation](inline-candidate-validation.txt).

Before the single-writer revision, a separate sequence-only diagnostic repeated
the earlier extension implementation and harness with JIT disassembly enabled.
Both versions completed two cases and
four process launches, with response validation on every launch:

| Count | Before (ns/batch) | After (ns/batch) | Time change | Before → after (KB/batch) |
| --- | ---: | ---: | ---: | ---: |
| 32 | 283.8 ± 0.58 | 286.6 ± 0.78 | +0.99% | 1.57 → 1.57 |
| 256 | 2,227.8 ± 4.87 | 2,217.2 ± 8.78 | -0.48% | 12.07 → 12.07 |

The larger 3–6% sequence slowdown seen in the first full candidate run did not
reproduce here. The 32-entry case remained slightly slower; intervals overlap at
256 entries. Generated Tier1 code reloads Count in the baseline loops. The
candidate caches Count in `AssignResponses`, but tail-dispatches to that helper
after materialization, adding a helper boundary. Fewer property reads alone
therefore do not establish lower execution time. These diagnostic timings are
reported separately because JIT output was enabled and the final implementation
has since removed the shared assignment helper and unset prepass.

Raw data: [before](sequence-repeat-before.csv), [after](sequence-repeat-after.csv),
[before validation](sequence-repeat-before-validation.txt),
[after validation](sequence-repeat-after-validation.txt).

## Temporary-array pooling experiment

These measurements precede removal of response-slot atomics and the unset
prepass. They compare two implementations with the same earlier contract and
are retained as exploratory evidence, rather than current factory timings. The
checked-in pooling harness now follows the final one-pass assignment contract.

[ResponseFactoryBufferBenchmarks](../../../tests/RequestBatcher.Benchmarks/InMemory/ResponseFactoryBufferBenchmarks.cs)
compares the candidate public factory overload with a benchmark-local pooled
implementation at batch sizes 1/32/256 for `int` and `string` responses. Both cache
the stable count in each phase, compute every response before validating and
assigning entries, construct fresh entries, and validate every assigned result.
Response values and the factory delegate are prepared outside measurement, so
string payload creation does not obscure buffer costs. The pooled path returns
buffers in `finally` and clears reference-containing arrays. Only the first logical
count of the rented array is used. This experiment does not pool caller-visible
result arrays, whose lifetime belongs to the caller.

The job uses two launches, three warmups, eight measurement iterations, and
automatic calibration. Results are per batch and include entry construction and
validation, rather than isolated `Rent`/`Return` timings.

| Response | Count | Owned array (ns/batch) | Pool (ns/batch) | Time change | Owned → pool (B/batch) |
| --- | ---: | ---: | ---: | ---: | ---: |
| `int` | 1 | 16.77 ± 0.081 | 22.19 ± 0.325 | +32.32% | 96 → 64 |
| `int` | 32 | 266.82 ± 1.989 | 270.99 ± 3.465 | +1.56% | 1,456 → 1,304 |
| `int` | 256 | 2,122.26 ± 36.985 | 2,125.95 ± 17.344 | +0.17% | 11,312 → 10,264 |
| `string` | 1 | 36.81 ± 0.436 | 43.00 ± 0.703 | +16.82% | 96 → 64 |
| `string` | 32 | 671.80 ± 8.887 | 677.95 ± 3.434 | +0.92% | 1,584 → 1,304 |
| `string` | 256 | 5,127.27 ± 74.758 | 5,174.30 ± 43.269 | +0.92% | 12,336 → 10,264 |

Pooling reduces allocated bytes in every case. It is clearly slower for count
one; intervals overlap at counts 32/256, providing no demonstrated timing gain.
The final implementation keeps one owned temporary array. This decision applies
to these bounded workloads, not every response size or allocation-pressure profile.
Reference clearing and pool traffic are included rather than inferred from
allocation counts alone. Count caching is present on both sides, so its benefit
is not attributed to pooling.

All 12 cases and 24 process launches succeeded; every launch reported successful
response validation. Raw data: [int](pooling-int.csv), [string](pooling-string.csv),
[validation](pooling-validation.txt).

## Named tracing

[NamedTracingHotPathBenchmarks](../../../tests/RequestBatcher.Benchmarks/InMemory/NamedTracingHotPathBenchmarks.cs)
uses a named request-only batcher, synchronous handlers, 256 distinct requests,
batch limit 32, concurrency eight, and capacity 256. Single and explicit submissions
run with no listener, a propagation-only listener, or a recording listener.
Each listener mode runs with normal or suppressed submission-context flow;
suppression ends before awaiting the public Task, and caller-owned flow state is
verified after submission. This includes the observer's no-captured-context path.
Each wave validates every ID and dispatch bounds; cleanup verifies completed
span counts, names, and sampling flags. No metrics provider or exporter is installed.
The job uses two launches, three warmups, ten measurement iterations, and automatic
calibration. Results are per wave.
This workload controls submission suppression, rather than forcing completion-thread
suppression. Deterministic tests cover the double-suppressed wrapper; the matrix
does not isolate that wrapper's allocation cost.

| Listener | Submission flow | Submission | Before (µs/wave) | After (µs/wave) | Time change | Before → after (KB/wave) |
| --- | --- | --- | ---: | ---: | ---: | ---: |
| Disabled | Normal | Explicit | 44.13 ± 0.245 | 44.77 ± 1.260 | +1.5% | 35.01 → 35.01 |
| Disabled | Normal | Singles | 84.36 ± 6.454 | 89.01 ± 8.543 | +5.5% | 57.34 → 57.37 |
| Disabled | Suppressed | Explicit | 44.46 ± 0.299 | 44.20 ± 0.785 | -0.6% | 35.01 → 35.01 |
| Disabled | Suppressed | Singles | 88.09 ± 6.395 | 81.97 ± 4.277 | -6.9% | 57.45 → 57.61 |
| Propagation | Normal | Explicit | 50.01 ± 0.274 | 50.20 ± 0.672 | +0.4% | 44.61 → 43.86 |
| Propagation | Normal | Singles | 129.06 ± 4.465 | 162.27 ± 4.441 | +25.7% | 289.79 → 293.62 |
| Propagation | Suppressed | Explicit | 47.97 ± 0.357 | 49.37 ± 0.571 | +2.9% | 44.85 → 44.1 |
| Propagation | Suppressed | Singles | 113.99 ± 3.715 | 134.72 ± 13.020 | +18.2% | 338.37 → 334.88 |
| Recorded | Normal | Explicit | 49.30 ± 0.156 | 51.12 ± 0.496 | +3.7% | 48.23 → 47.48 |
| Recorded | Normal | Singles | 180.93 ± 13.697 | 176.08 ± 2.991 | -2.7% | 334.08 → 333.74 |
| Recorded | Suppressed | Explicit | 48.90 ± 0.178 | 52.02 ± 0.432 | +6.4% | 48.48 → 47.73 |
| Recorded | Suppressed | Singles | 193.10 ± 2.527 | 200.48 ± 4.893 | +3.8% | 394.77 → 393.96 |

Traced explicit waves save 0.75 KB. Recorded explicit time nevertheless increases
by 3.7% with normal submission flow and 6.4% with suppressed flow; the exported
intervals do not overlap the baseline in these cases. Propagation-only concurrent
singles also regress in this suite. Disabled-listener controls are approximately
flat within their variation. This matrix demonstrates allocation savings, not an
overall tracing CPU gain.

Concurrent singles also formed different numbers of handler batches. Across all
validation records, normal-flow propagation runs averaged 56.95 batches per wave
before and 71.86 after; suppressed-flow runs averaged 40.80 and 48.55. Explicit
submissions consistently formed eight batches per wave. These aggregates include
calibration and warmup, so they cannot normalize the measured timing rows. They
do show that the single-call differences include a change in batch formation;
the full increase cannot be assigned to a fixed per-call context operation.

The count-cached candidate reads the stable snapshot count once for the newly
indexed sampling and link scans. A prior candidate without that hoist measured
52.43 ± 0.911 µs and 52.51 ± 1.583 µs for the two recorded explicit cases, compared
with 51.12 ± 0.496 µs and 52.02 ± 0.432 µs after hoisting. These point estimates are
lower, but intervals overlap; the count change neither establishes the complete
cause nor removes the regression against the original baseline. Replacing an
interface enumerator with interface indexing exchanges costs rather than proving
a speedup. The observer-only diagnostic below tests one contribution; it does not
isolate every remaining change.

Raw data: [before](before-named-tracing.csv), [after](after-named-tracing.csv),
[pre-count candidate](before-count-named-tracing.csv),
[pre-count validation](before-count-validation.txt),
[pre-count process diagnostics](before-count-diagnostics.txt).

## Completion-observer isolation diagnostic

An additional pair keeps the count-cached candidate and benchmark harness fixed,
changing only `CallCompletionObserver.cs` back to the baseline implementation in
the control. Both run the two Recorded explicit-submission cases with the existing
job settings, sequentially on the same .NET 10 runtime. Each variant completes four
process launches and validates every request, dispatch bound, and span name.

| Submission flow | Old observer control (µs/wave) | Shared-helper observer (µs/wave) | Time change | Allocation (KB/wave, both) |
| --- | ---: | ---: | ---: | ---: |
| Normal | 49.73 ± 1.614 | 53.08 ± 2.689 | +6.7% | 47.48 |
| Suppressed | 51.60 ± 0.463 | 51.76 ± 0.540 | +0.3% | 47.73 |

The normal-flow point estimate suggests a possible observer contribution, but
the intervals overlap; this single pair does not establish the size or cause
decisively. The suppressed-flow result is approximately flat. In particular,
these data do not prove that capturing a fallback context explains the full
regression. The old normal-flow implementation already uses `ExecutionContext.Run`.

The old suppressed-flow control has the known arbitrary-`AsyncLocal` leak.
This performance harness installs no listener that mutates such a value, so its
successful validation is not evidence that reverting the correctness fix is safe.
Production retains full context restoration. The caller-side `Activity.Current`
restore and completion-side `ExecutionContext.Run` cover different invocation
boundaries: capturing submission context does not restore the submitting caller
when the synchronous submission method returns its Task.

Observer SHA-256: current
`1dc47c010a9b18781e26d48f7439d9866dd5f98eeccf3da90520cb14e1895de9`;
control
`e47e623366c20c275206db2ebed126fa24a96e0bb4a8fcff901db7998d7badf3`.
The control file matches the baseline commit. Raw data:
[old observer](observer-old.csv), [current observer](observer-current.csv),
[old validation](observer-control-validation.txt),
[current validation](observer-current-validation.txt).

## SDK tracing with simulated I/O

[TracingIoOverheadBenchmarks](../../../tests/RequestBatcher.Benchmarks/InMemory/TracingIoOverheadBenchmarks.cs)
uses 1,024 distinct requests per wave, single submissions or explicit groups of 32,
batch limit 32, concurrency eight, capacity 1,024, and 20 ms handler operations.
The real SDK runs with tracing disabled or root sampling at 0%, 1%, 10%, or 100%.
Every ID is validated exactly once. Two launches, four warmups, twelve measurement
iterations, and four waves per iteration are used. Results are per wave.

| Tracing | Submission | Before (ms/wave) | After (ms/wave) | Time change | Before → after (KB/wave) |
| --- | --- | ---: | ---: | ---: | ---: |
| Disabled | Explicit | 87.20 ± 0.447 | 85.11 ± 1.486 | -2.4% | 168.32 → 168.57 |
| Disabled | Singles | 106.49 ± 2.441 | 101.74 ± 2.926 | -4.5% | 284.38 → 283.15 |
| 0 | Explicit | 87.61 ± 0.564 | 84.60 ± 0.366 | -3.4% | 219.16 → 219.37 |
| 0 | Singles | 107.16 ± 2.677 | 103.18 ± 1.957 | -3.7% | 983.29 → 982.88 |
| 1 | Explicit | 87.55 ± 1.133 | 84.28 ± 0.461 | -3.7% | 226.51 → 219.67 |
| 1 | Singles | 107.32 ± 2.152 | 102.92 ± 2.891 | -4.1% | 1011.37 → 1032.91 |
| 10 | Explicit | 87.24 ± 0.638 | 86.46 ± 1.886 | -0.9% | 222.09 → 222.2 |
| 10 | Singles | 108.02 ± 2.396 | 102.62 ± 2.670 | -5.0% | 1146.18 → 1145.08 |
| 100 | Explicit | 87.44 ± 0.452 | 86.27 ± 2.227 | -1.3% | 233.74 → 236.18 |
| 100 | Singles | 108.68 ± 1.582 | 101.65 ± 3.182 | -6.5% | 1313.81 → 1316.83 |

The candidate has lower wall-time point estimates across this matrix, including
the disabled control. Because the waves are dominated by simulated I/O, scheduling,
and batch formation, these differences must not be attributed directly to tracing
CPU changes. Allocation also varies with batch counts and sampling decisions.

Raw data: [before](before-sdk-io.csv), [after](after-sdk-io.csv).

## Correctness and reproduction

Focused tests cover response-factory failure and factory-side count changes before
assignment, duplicate rejection with preservation of earlier assignments,
one-pass enumeration, rejection before input enumeration,
cancellation/shutdown during materialization, caller mutation after admission,
ordered results, and tracing context restoration. Response stress tests exercise
concurrent single, explicit, and mixed submissions with and without partition keys,
checking every response and exactly-once handling on both supported runtimes.
The SDK tracing stress suite runs without Docker and verifies real batch links,
mixed sampling, cancellation, failure, and draining.

Final validation passed 615 unit/stress cases on each of `net8.0` and `net10.0`,
including 180,000 response-stress requests per runtime. The five SDK tracing stress
cases also passed; their 0%/100% mixed-submission waves cover 32,768 requests.
The Release solution build reported zero warnings/errors, changed-file formatting
verification passed, and both production packages packed successfully.

Each initial matrix source version completed all 34 cases and 68 process launches
with successful correctness validation and child exit code zero. The candidate
matrix took 8 minutes 32 seconds before the final response-slot revision.
BDN reported priority-permission, outlier, multimodal
distribution, and short response-iteration warnings. Three suppressed-single
baseline launches also reported exception counters; no child or validation failed.
The candidate matrix had no such exception diagnostics.
Raw records: [before validation](before-validation.txt),
[after validation](after-validation.txt), [before diagnostics](before-diagnostics.txt),
[after diagnostics](after-diagnostics.txt).

The final response-only run completed all 12 cases and 24 process launches in
1 minute 52 seconds. All eight assignment and sixteen response-wave validation
records passed; every child exited zero. The frozen final manifest still matched
all 17 source paths after measurement. Raw records:
[final response validation](final-response-validation.txt),
[final response diagnostics](final-response-diagnostics.txt).

The new completion-listener test writes an arbitrary `AsyncLocal` and independently
varies submission and completion flow suppression. Before the fallback fix, both
suppressed-submission cases failed on each runtime because the worker retained
the listener's value; both captured-context cases passed. All four combinations
pass after the fix. The shared `ExecutionContextIsolation.RunSynchronously` helper
uses `ExecutionContext.Run` to restore the completion thread's entire context before
publishing the original Task. Its `capturedContext` argument is the submission-time
snapshot; when absent, it snapshots the completion thread's current values instead.
If both submission and completion flow are suppressed, it temporarily enables capture,
restores suppression before Run, and keeps suppression inside the callback with a
small state wrapper. This path has additional allocation; it uses no asynchronous
method or blocking wait. The normal captured-context path passes its snapshot directly.
Pending-call tests also assert that `StartActivity` makes the Execute span current
during submission, the caller's parent is restored immediately, and that same span
ends after completion. Completed success/failure/cancellation paths assert immediate
parent restoration in both tracing overloads. The eight shared-helper cases also
verify same-thread execution and restoration of Activity, AsyncLocal, synchronization
context, and caller-owned suppression. Exception filters see the restored values
before the callback error reaches the caller.

Build each isolated source tree with the repository SDK:

```bash
dotnet restore tests/RequestBatcher.Benchmarks/RequestBatcher.Benchmarks.csproj \
  --ignore-failed-sources -p:NuGetAudit=false -m:1
dotnet build tests/RequestBatcher.Benchmarks/RequestBatcher.Benchmarks.csproj \
  --configuration Release --no-restore --disable-build-servers \
  -m:1 -p:UseSharedCompilation=false
```

Run each source version separately, with an isolated artifact directory:

```bash
NuGetAudit=false MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_USE_MSBUILD_SERVER=0 \
dotnet run --project tests/RequestBatcher.Benchmarks/RequestBatcher.Benchmarks.csproj \
  --configuration Release --no-build --no-restore -- \
  --filter '*ResponseHotPathBenchmarks*' '*ResponseAssignmentBenchmarks*' \
    '*NamedTracingHotPathBenchmarks*' '*TracingIoOverheadBenchmarks*' \
  --unrollFactor 1 --artifacts /private/tmp/requestbatcher-response-before
```

The final response remeasurement uses the unchanged jobs and a separate output:

```bash
NuGetAudit=false MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_USE_MSBUILD_SERVER=0 \
dotnet run --project tests/RequestBatcher.Benchmarks/RequestBatcher.Benchmarks.csproj \
  --configuration Release --no-build --no-restore -- \
  --filter '*ResponseHotPathBenchmarks*' '*ResponseAssignmentBenchmarks*' \
  --unrollFactor 1 --artifacts /private/tmp/requestbatcher-single-writer-final
```

The buffer command exercises the final checked-in harness. Reproducing the
historical table requires the earlier matrix-source implementation instead:

```bash
NuGetAudit=false MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_USE_MSBUILD_SERVER=0 \
dotnet run --project tests/RequestBatcher.Benchmarks/RequestBatcher.Benchmarks.csproj \
  --configuration Release --no-build --no-restore -- \
  --filter '*ResponseFactoryBufferBenchmarks*' \
  --unrollFactor 1 --artifacts /private/tmp/requestbatcher-response-pooling-matched
```

To repeat the sequence diagnostic, run each source tree separately with a distinct
log and JIT output file:

```bash
DOTNET_JitDisasm='RequestBatcher.RequestBatchEntryExtensions:*' \
DOTNET_JitStdOutFile=/private/tmp/requestbatcher-sequence-jit.txt \
NuGetAudit=false MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_USE_MSBUILD_SERVER=0 \
dotnet run --project tests/RequestBatcher.Benchmarks/RequestBatcher.Benchmarks.csproj \
  --configuration Release --no-build --no-restore -- \
  --filter '*ResponseAssignmentBenchmarks.OrderedSequenceAssignment*' \
  --unrollFactor 1 --artifacts /private/tmp/requestbatcher-sequence-diagnostic
```

Correctness commands:

```bash
dotnet test tests/RequestBatcher.Tests/RequestBatcher.Tests.csproj \
  --configuration Release --no-build --no-restore
dotnet test tests/it/RequestBatcher.Observability.IntegrationTests/RequestBatcher.Observability.IntegrationTests.csproj \
  --configuration Release --no-build --no-restore \
  --filter 'FullyQualifiedName~TracingStressTests'
```
