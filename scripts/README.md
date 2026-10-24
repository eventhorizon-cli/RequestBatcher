# Repository Scripts

This directory contains file-based C# tools for performance comparisons, allocation diagnostics, and scheduling
experiments. Benchmark workloads live in the [benchmark project](../tests/RequestBatcher.Benchmarks/README.md);
recorded results and their limitations live in the [evaluation index](../docs/evaluations/README.md).
Follow the repository's [contributor instructions](../AGENTS.md) when maintaining these tools.

## Running Scripts

Run commands from the repository root with the SDK selected by [global.json](../global.json). Each entry point supports
`--help`, for example:

```bash
dotnet run --file scripts/CompareTracingIoOverhead.cs -- --help
```

The current script entry points target .NET 8 and require its runtime as well as the repository SDK. A runner's target
framework does not determine the runtime of every benchmark it launches; check the script and evaluation guide before
comparing measurements. Package restore requires access to the configured NuGet sources.

## Tracing and Allocation Tools

| Entry point | Purpose and arguments | Detailed guide |
| --- | --- | --- |
| [CompareTracingIoOverhead.cs](CompareTracingIoOverhead.cs) | Compare published 0.2.4 and 0.3.2 with simulated 20 ms downstream calls across tracing modes. Use `--compare-source` to compare the current source, optionally with `--baseline-source PATH` and `--include-legacy`. Optional positional argument: artifact parent directory. | [Tracing I/O comparison](../docs/evaluations/tracing-io-overhead/README.md), [source comparison](../docs/evaluations/tracing-io-overhead/shared-completion.md) |
| [CompareTracingOverhead.cs](CompareTracingOverhead.cs) | Compare 0.2.4 and the current source with a lightweight handler. Optional artifact parent directory and `--prepare-only`; `TRACING_OVERHEAD_FILTER` selects BenchmarkDotNet cases. | [Tracing overhead](../docs/evaluations/tracing-overhead/README.md) |
| [ProfileTracingAllocations.cs](ProfileTracingAllocations.cs) | Run the 20 ms allocation probe: `<Disabled\|Sample0\|Sample100> [waves=500]`. Emits measurement markers and allocation totals; use `dotnet-trace` to capture a trace. | [Allocation profiling and capture commands](../docs/evaluations/tracing-io-overhead/allocation-profile.md) |
| [AnalyzeAllocationTrace.cs](AnalyzeAllocationTrace.cs) | Analyze an existing marked EventPipe trace: `<trace.nettrace> <output-prefix>`. Writes `.types.csv`, `.stacks.csv`, and `.summary.txt`; allocation samples are estimates. | [Allocation analysis](../docs/evaluations/tracing-io-overhead/allocation-profile.md) |

Both comparison runners retain isolated projects, package evidence, and BenchmarkDotNet reports in a unique `run-*`
directory. Their default parent directories are `requestbatcher-tracing-io` and `requestbatcher-tracing-overhead`,
respectively, under the system temporary directory. Profiling requires the additional `dotnet-trace` tool described in
its guide. These tracing tools do not require Docker or an OpenTelemetry backend.

## Scheduling Experiments

These tools build isolated source copies with experimental scheduling instrumentation. Read their guides before
interpreting the results; the copies are evaluation fixtures rather than supported production configuration.

| Entry point | Purpose and arguments | Detailed guide |
| --- | --- | --- |
| [EvaluateScheduling.cs](scheduling-evaluation/EvaluateScheduling.cs) | Evaluate preloaded bursts with asynchronous, CPU, and blocking handlers. Requires `--output PATH`; supports `--self-check` and `--prepare-only`. | [Burst scheduling](scheduling-evaluation/README.md) |
| [EvaluateHttpScheduling.cs](scheduling-evaluation/EvaluateHttpScheduling.cs) | Evaluate real HTTP calls against a temporary local server. Requires `--output PATH`; supports `--smoke`, `--batch-control`, and `--latency-sweep`. | [HTTP scheduling](scheduling-evaluation/README.io.md) |
| [EvaluateArrivalScheduling.cs](scheduling-evaluation/EvaluateArrivalScheduling.cs) | Evaluate arrivals over time and handler utilization. Requires `--output PATH`; supports `--smoke`, `--prepare-only`, and optional PostgreSQL or Redis workloads. | [Arrival scheduling](scheduling-evaluation/README.arrival.md) |

The scheduling guides describe additional workload arguments, generated reports, and retained temporary source copies.
In-memory and HTTP experiments require no external services; the HTTP runner starts and stops its own local server.
Database experiments require a dedicated test database and use the connection settings documented in the arrival guide.

[Program.cs](scheduling-evaluation/Program.cs), [IoProgram.cs](scheduling-evaluation/IoProgram.cs), and
[IoServer.cs](scheduling-evaluation/IoServer.cs) are source templates copied into temporary projects by the scheduling
runners, not standalone file-based entry points.

## Maintenance

Keep this index synchronized when adding, renaming, or changing an entry point. Document its arguments, prerequisites,
artifact locations, and validation in its linked guide. Keep generated reports and diagnostic traces outside tracked
source files; record reviewed findings under [docs/evaluations](../docs/evaluations/README.md).
