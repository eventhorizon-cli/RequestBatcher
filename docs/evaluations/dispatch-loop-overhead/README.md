# Dispatch loop bookkeeping and shutdown

Removing redundant in-flight bookkeeping reduces allocation in every measured configuration.
With synchronous handlers and `BatchSize=1`, elapsed time per request falls by 7.6% for
single submissions and 15.4% for explicit submissions. The `BatchSize=32` measurements
do not establish a timing improvement, and the 20 ms handler cases are effectively unchanged.

The candidate uses semaphore permits to join active executions in `DisposeAsync`,
removes the dispatcher's per-batch lock and drain completion source, keeps one coordinator
shutdown completion source for its lifetime, and selects queued requests by index.
It also calls `CreatePullConsumer` directly. Consumer construction and disposal are
outside the measured region, so these results do not measure startup or shutdown latency.
See the [dispatch design](../../batch-dispatch-redesign.md) for the lifecycle contract.

## Environment and comparison

Measured on 2026-11-03 with BenchmarkDotNet 0.15.8, Apple M2 Max, 12 logical cores,
macOS 27.2 (26B123), Arm64, SDK 10.0.100, and **.NET 10.0.0**
(`10.0.25.52411`, Arm64 RyuJIT armv8.0-a, concurrent workstation GC).
Both versions used the same machine, runtime, options, and benchmark source.
The baseline ran first, then the candidate; no local builds or tests ran concurrently
with either measured run. No containers or external services were used.

- Baseline: `093cfe7d5de2db2ccaba33e874b74477c7d1100b`, with the new
  `DispatchLoopOverheadBenchmarks.cs` workload copied into an isolated source archive.
- Candidate: the same archive with the working-tree versions of
  `RequestBatchCoordinator.cs`, `Scheduling/BatchDispatchLoop.cs`, and
  `Scheduling/RequestBatchConsumer.cs`, plus the identical workload.
- Each version completed 12 cases and 24 process launches. Both runs exited successfully.
  The 24 validation records per version report exactly-once handling; the overhead
  workload also explicitly records successful batch-size and concurrency checks.

Candidate production SHA-256 values, checked against the final working-tree files:

```text
63a21bcb3ac66ea641f00580708974f8955ad70900f5ea01493d24ffe32a74df  RequestBatchCoordinator.cs
c6ca7738279ab1d3d1892a8ec75c468a9a001554a1408e13ba6d20c4f89cb25a  Scheduling/BatchDispatchLoop.cs
c6acd256e1f015fa0143515c426ef5f131a6172937b45225a72a517f2531f2b9  Scheduling/RequestBatchConsumer.cs
```

The overhead workload SHA-256 is
`77eb82aa28f2b7522165479df001273022ea303984794465759eabab26bd0c18`
in both archives and the working tree.

## Synchronous submission overhead

[DispatchLoopOverheadBenchmarks](../../../tests/RequestBatcher.Benchmarks/InMemory/DispatchLoopOverheadBenchmarks.cs)
submits 1,024 distinct integer requests per wave through either concurrent single calls
or one explicit submission. It uses one unkeyed partition, concurrency eight, capacity
1,024, batch limits 1/32, and a synchronous singleton handler. No telemetry listeners
are registered. Each wave checks every request ID exactly once and enforces dispatch
bounds. Input arrays and DI setup are outside measurement; submission, completion,
handler accounting, and validation are included in both versions.

The job uses two launches, three warmup iterations, ten measurement iterations, and
automatic invocation calibration. `OperationsPerInvoke=1024` makes both time and
allocation **per request**, including amortized wave overhead.

All tables show the mean with the 99.9% confidence-interval margin after `±`.
Changes are `(after / before - 1) × 100`, calculated from the exported rounded values;
negative values mean less elapsed time or allocation. They are point estimates,
not statistical significance claims.

| Submission | Batch limit | Before (ns/request) | After (ns/request) | Time change | Before (B/request) | After (B/request) | Allocation change |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| Concurrent singles | 1 | 692.2 ± 18.23 | 639.4 ± 13.48 | -7.63% | 404 | 281 | -30.45% |
| One explicit submission | 1 | 271.9 ± 2.32 | 230.0 ± 1.78 | -15.41% | 305 | 185 | -39.34% |
| Concurrent singles | 32 | 256.0 ± 41.14 | 266.0 ± 51.48 | +3.91% | 240 | 226 | -5.83% |
| One explicit submission | 32 | 115.7 ± 17.39 | 125.0 ± 0.80 | +8.04% | 144 | 140 | -2.78% |

The batch-limit-one intervals do not overlap, supporting a time reduction for these
workloads. The batch-limit-32 point estimates are slower, but their intervals overlap
and the baseline or single-submission measurements have substantial variation. They
provide no evidence of a timing improvement or a confirmed regression. Allocation
falls in all four cases; batching amortizes the removed per-batch bookkeeping.

Raw data: [before](before-overhead.csv), [after](after-overhead.csv).
The CSV `Ratio` columns compare submission methods within one run; they are not
before/after ratios.

## Dispatch capacity with synchronous and asynchronous handlers

[DispatchCapacityBenchmarks.RequestBatcherAsync](../../../tests/RequestBatcher.Benchmarks/InMemory/DispatchCapacityBenchmarks.cs)
submits 4,096 distinct requests in one explicit submission with batch limit 32.
The matrix varies concurrency 8/32, one unkeyed or sixteen keyed partitions, and
synchronous completion or `Task.Delay(20)`. The existing direct-handler control is
not selected for this before/after comparison. Each wave validates every ID exactly
once, batch limits, and concurrency limits.

This job uses two launches, three warmups, eight measurement iterations, and four
waves per iteration. Time and allocation below are **per 4,096-request wave**;
KiB means 1,024 bytes (the CSV labels these values `KB`).

| Handler delay (ms) | Concurrency | Partitions | Before (µs/wave) | After (µs/wave) | Time change | Allocation before → after (KiB/wave) | Allocation change |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 0 | 8 | 1 | 1,008.7 ± 65.06 | 985.0 ± 53.67 | -2.35% | 575.10 → 560.02 | -2.62% |
| 0 | 8 | 16 | 1,082.9 ± 40.78 | 1,048.2 ± 34.27 | -3.20% | 633.67 → 618.59 | -2.38% |
| 0 | 32 | 1 | 962.8 ± 32.08 | 923.3 ± 28.23 | -4.10% | 575.10 → 560.02 | -2.62% |
| 0 | 32 | 16 | 1,064.7 ± 72.43 | 1,022.0 ± 18.93 | -4.01% | 633.67 → 618.59 | -2.38% |
| 20 | 8 | 1 | 341,004.5 ± 2,536.56 | 339,025.2 ± 1,796.08 | -0.58% | 662.35 → 657.91 | -0.67% |
| 20 | 8 | 16 | 338,796.8 ± 1,411.54 | 339,429.4 ± 2,053.44 | +0.19% | 719.66 → 713.17 | -0.90% |
| 20 | 32 | 1 | 86,208.7 ± 1,057.44 | 85,871.4 ± 837.35 | -0.39% | 666.85 → 661.85 | -0.75% |
| 20 | 32 | 16 | 86,697.2 ± 817.34 | 86,124.9 ± 877.61 | -0.66% | 714.77 → 712.35 | -0.34% |

Synchronous waves allocate 15.08 KiB less, with 2.4–2.6% lower allocation.
Their elapsed-time point estimates improve by 2.4–4.1%, but the intervals overlap
and BDN warns that these iterations are shorter than its recommended 100 ms.
The 20 ms cases vary from -0.66% to +0.19% in elapsed time, also with overlapping
intervals. They do not establish a meaningful throughput change for simulated I/O.

Raw data: [before](before-capacity.csv), [after](after-capacity.csv).
Validation records: [before](before-validation.txt), [after](after-validation.txt).

## Hot-path audit and correctness

The linked dispatcher cancellation source remains: it is created once per loop and
wakes a pending pull or slot wait after an infrastructure failure. Handler execution
continues to receive the original owner token. Cancellation notification does not
wait for detached execution cleanup, so disposal still acquires all semaphore permits
after the loop has stopped. The coordinator is the dispatcher's sole disposal owner;
the dispatcher has no disposal lock or cached disposal task. Public coordinator
disposal still shares one task and supports concurrent calls.

The coordinator's shutdown completion source is allocated once for its lifetime.
Closing admission publishes the stopping state before checking the pending count;
the last completion rechecks the count after observing shutdown. This avoids a lost
wakeup without taking the lifecycle lock on request completion. The selection loop
uses indexed access to avoid an `IReadOnlyList<T>` enumerator per batch.

Semaphore acquisition/release, per-request completion, and independent handler request
arrays remain necessary for backpressure and completion ownership. Disabled tracing,
metrics, and logging already skip their enabled-path work. The producer links tokens
only when a caller supplies a cancellable token. These measurements do not isolate
the contribution of each individual change or quantify enabled telemetry overhead.

Deterministic tests cover slot exhaustion, loop cancellation, finite consumer
exhaustion with one slot still occupied, completion callbacks that outlive caller
completion, fatal execution failure while another handler runs, repeated idle periods
before shutdown, and concurrent/repeated public disposal. Metrics tests compare
recorded handler-batch sizes with actual invocations because one explicit submission
may produce multiple handler batches.

The final complete test run passed **589 tests on .NET 8 and 589 on .NET 10**.
This includes all six `ResponseStressTests` configurations: concurrent single,
explicit, and mixed submissions, each with and without partition keys. Eight
producers execute three rounds of 10,000 requests per configuration, validating
180,000 responses per framework (360,000 total), every response against its input,
explicit-submission response order, and exactly-once handling. The targeted
scheduling/coordinator/backlog tests passed 80 cases per framework; the corrected
metrics tests passed 14 per framework before the complete rerun.
The Release solution build completed with zero warnings and zero errors. Formatting
verification passed for the twelve C# files changed by this task. Both library
projects also passed Release package validation with single-process MSBuild.

Validation commands, run after the measured processes finished:

```bash
dotnet test tests/RequestBatcher.Tests/RequestBatcher.Tests.csproj \
  --configuration Release --no-restore -m:1 -p:UseSharedCompilation=false \
  --filter 'FullyQualifiedName~RequestBatcherMetricsTests' \
  --logger 'console;verbosity=normal'
dotnet test tests/RequestBatcher.Tests/RequestBatcher.Tests.csproj \
  --configuration Release --no-build --no-restore -m:1 \
  --logger 'console;verbosity=normal'
dotnet build RequestBatcher.slnx --configuration Release --no-restore \
  --disable-build-servers -m:1 -p:UseSharedCompilation=false
dotnet pack src/RequestBatcher/RequestBatcher.csproj \
  --configuration Release --no-build --no-restore --disable-build-servers \
  -m:1 -p:UseSharedCompilation=false
dotnet pack src/RequestBatcher.OpenTelemetry/RequestBatcher.OpenTelemetry.csproj \
  --configuration Release --no-build --no-restore --disable-build-servers \
  -m:1 -p:UseSharedCompilation=false
```

## Reproduce

Use isolated baseline and candidate directories with the same overhead workload.
In each directory, restore and build before running the measured command:

```bash
dotnet restore tests/RequestBatcher.Benchmarks/RequestBatcher.Benchmarks.csproj \
  --ignore-failed-sources -p:NuGetAudit=false -m:1
dotnet build tests/RequestBatcher.Benchmarks/RequestBatcher.Benchmarks.csproj \
  --configuration Release --no-restore --disable-build-servers \
  -m:1 -p:UseSharedCompilation=false
NuGetAudit=false MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_USE_MSBUILD_SERVER=0 \
dotnet run --project tests/RequestBatcher.Benchmarks/RequestBatcher.Benchmarks.csproj \
  --configuration Release --no-build --no-restore -- \
  --filter '*DispatchLoopOverheadBenchmarks*' '*DispatchCapacityBenchmarks.RequestBatcherAsync*' \
  --unrollFactor 1 --artifacts /private/tmp/requestbatcher-dispatch-before
```

Use `/private/tmp/requestbatcher-dispatch-after` for the candidate artifacts.
The full logs from this run are `/private/tmp/requestbatcher-dispatch-before.log`
and `/private/tmp/requestbatcher-dispatch-after.log`. Earlier sandboxed attempts
that could not build the generated benchmark executable are excluded; no failed
or unexecuted benchmark row is used above. High-priority scheduling was unavailable
in both successful runs.

These are finite in-process waves, not per-request tail-latency or sustained
arrival-rate measurements. The synchronous handler only records IDs, making small
dispatch costs visible. Real downstream work, sparse arrivals, scope disposal,
caller cancellation rates, and retained heap size are not measured here.
