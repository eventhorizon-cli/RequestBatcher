# Repository C# scripts

## Contract and compatibility

Repository automation uses file-based C# applications supported by the SDK pinned in `global.json`.
Run scripts from the repository root with `dotnet run --file <script.cs> -- <arguments>`.
Scripts use C# 12, base libraries, explicit runtime targets, and no separate scripting runtime or project file.
This does not change library target frameworks or dependencies.

Scheduling evaluation entry points are `scripts/scheduling-evaluation/EvaluateScheduling.cs`, `EvaluateHttpScheduling.cs`, and `EvaluateArrivalScheduling.cs`.
They preserve existing command-line flags, defaults, case sets, JSON output fields, temporary source isolation,
prototype hooks, timeout behavior, and child-process cleanup. Source replacement requires exactly one match and
fails before a measurement if the expected source no longer matches. All instrumentation patches stay in scratch
directories. Child arguments use `ProcessStartInfo.ArgumentList`, never shell interpolation.
The arrival runner also copies the core project's package references into its scratch benchmark project so the
isolated runtime dependency manifest uses the same versions. It derives these references from the copied project;
the checkout's package references are unchanged.
Case order is deterministic with each runner's fixed seed; the C# random generator need not reproduce Python order.
Historical measurements remain historical artifacts and are not replaced by migration smoke results.

The tracing package comparison script also uses C# and downloads the published baseline package into an isolated
NuGet cache. Both builds run with the repository SDK. Each execution creates a fresh artifact directory and retains
raw BenchmarkDotNet results and package SHA-512 evidence.

`CompareTracingIoOverhead.cs` isolates the 20 ms tracing benchmark in temporary projects referencing published
RequestBatcher 0.2.4 and 0.3.2 packages. Both use identical benchmark source, runtime, and dependencies, including
the OpenTelemetry SDK. The old package runs its two `Disabled` cases; 0.3.2 runs `Disabled`, `Sample0`, `Sample1`,
`Sample10`, and `Sample100`, using the real SDK sampler without an exporter. With `--compare-source`, the runner
copies `src/RequestBatcher` and `src/RequestBatcher.Analyzers` (excluding build outputs) plus the pinned SDK files into
the artifact directory, then compares the 0.3.2 package with that fixed source snapshot using the same ten cases.
The runner verifies resolved package/project assets, retains package hashes and complete reports, requires exactly two
CSV data rows for 0.2.4 and ten for each current-version scenario, rejects `NA` results, and fails on child-process
errors. It neither changes the checkout's package references nor exports telemetry.

`ProfileTracingAllocations.cs` reuses the tracing I/O benchmark workload in a standalone diagnostic process. It accepts
`Disabled`, `Sample0`, or `Sample100` and a positive measured wave count, warms up ten waves, then emits
`RequestBatcher-AllocationProbe` Start/Stop events around the measured waves. Each wave retains the benchmark's
exactly-once validation. It reports the process-wide allocated-byte delta separately from sampled allocation events.
Profile the compiled application with `dotnet-trace` using `gc-verbose` and the marker provider; setup, warmup, and
cleanup must not enter the allocation analysis window. Diagnostic timing is not a benchmark result.

`AnalyzeAllocationTrace.cs` reads the resulting nettrace with Microsoft's TraceEvent library. It requires exactly one
matched marker window and nonempty allocation samples, aggregates GC allocation-tick weights by type and allocation
stack, and writes CSV evidence. Sample counts are not object counts, and sampled weights are estimates, not exact
per-type byte totals. TraceEvent is a script-only dependency; production dependencies remain unchanged. Keep raw
trace files outside the repository and commit compact reports with tool versions, arguments, and limitations.

The observability sample's traffic generator is a file-based C# application. Docker publishes it using the pinned
SDK and runs the resulting assembly on .NET 8. Cart and Pricing remain the only business project files.

## Validation

Compile every script and validate help and invalid arguments. Run scheduling self-check, HTTP smoke, and arrival
smoke against isolated source copies, checking expected result counts, JSON fields, and unchanged checkout sources.
Do not treat smoke runs as performance evidence. Verify the tracing comparison preparation and CLI without rerunning
expensive benchmarks unless the measured implementation changes. Exercise the traffic generator against the sample
and check graceful cancellation, complete responses, and decimal quote totals.
