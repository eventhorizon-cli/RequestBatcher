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

The observability sample's traffic generator is a file-based C# application. Docker publishes it using the pinned
SDK and runs the resulting assembly on .NET 8. Cart and Pricing remain the only business project files.

## Validation

Compile every script and validate help and invalid arguments. Run scheduling self-check, HTTP smoke, and arrival
smoke against isolated source copies, checking expected result counts, JSON fields, and unchanged checkout sources.
Do not treat smoke runs as performance evidence. Verify the tracing comparison preparation and CLI without rerunning
expensive benchmarks unless the measured implementation changes. Exercise the traffic generator against the sample
and check graceful cancellation, complete responses, and decimal quote totals.
