# Repository Scripts

This directory contains file-based C# tools for performance comparisons, allocation diagnostics, and scheduling
experiments. Benchmark workloads live in the [benchmark project](../tests/RequestBatcher.Benchmarks/README.md);
recorded results and their limitations live in the [evaluation index](../docs/evaluations/README.md).
Follow the repository's [contributor instructions](../AGENTS.md) when maintaining these tools.

Keep script entry points, implementations, and shared helpers in this directory, with reusable utilities under
[`Common`](Common/RequestBatcher.ScriptUtilities.csproj). Do not place script code in benchmark, test, or production
projects. Scripts may reference the production API they exercise; benchmark workloads remain in the benchmark project.

## Running Scripts

Run commands from the repository root with the SDK selected by [global.json](../global.json). Each entry point supports
`--help`, for example:

```bash
dotnet run --file scripts/CompareTracingIoOverhead.cs -- --help
```

The current script entry points and generated evaluation projects target .NET 10 with C# 12 and require the .NET 10
runtime as well as the repository SDK. A runner's target
framework does not determine the runtime of every benchmark it launches; check the script and evaluation guide before
comparing measurements. Package restore requires access to the configured NuGet sources.

## Tracing and Allocation Tools

| Entry point | Purpose and arguments | Detailed guide |
| --- | --- | --- |
| [CompareTracingIoOverhead.cs](CompareTracingIoOverhead.cs) | Compare published 0.2.4 and 0.3.2 with simulated 20 ms downstream calls across tracing modes. Use `--compare-source` to compare the current source, optionally with `--baseline-source PATH` and `--include-legacy`. Optional positional argument: artifact parent directory. | [Tracing I/O measurement and reproduction guide](../docs/evaluations/tracing-io-overhead/README.md), [.NET 10 allocation baseline](../docs/evaluations/tracing-io-overhead/single-partition-net10.md) |
| [CompareTracingOverhead.cs](CompareTracingOverhead.cs) | Compare 0.2.4 and the current source with a lightweight handler. Optional artifact parent directory and `--prepare-only`; `TRACING_OVERHEAD_FILTER` selects BenchmarkDotNet cases. | [Tracing I/O measurement and reproduction guide](../docs/evaluations/tracing-io-overhead/README.md) |
| [ProfileTracingAllocations.cs](ProfileTracingAllocations.cs) | Run the 20 ms allocation probe: `<Disabled\|Sample0\|Sample100> [waves=500]`. Emits measurement markers and allocation totals; use `dotnet-trace` to capture a trace. | [.NET 10 allocation baseline and reproduction](../docs/evaluations/tracing-io-overhead/single-partition-net10.md) |
| [AnalyzeAllocationTrace.cs](AnalyzeAllocationTrace.cs) | Analyze an existing marked EventPipe trace: `<trace.nettrace> <output-prefix>`. Writes `.types.csv`, `.stacks.csv`, and `.summary.txt`; allocation samples are estimates. | [Allocation diagnostics](../docs/evaluations/tracing-io-overhead/README.md#allocation-diagnostics) |

Both comparison runners retain isolated projects, package evidence, and BenchmarkDotNet reports in a unique `run-*`
directory. Their default parent directories are `requestbatcher-tracing-io` and `requestbatcher-tracing-overhead`,
respectively, under the system temporary directory. Profiling requires the additional `dotnet-trace` tool described in
its guide. These tracing tools do not require Docker or an OpenTelemetry backend.

## Scheduling Experiments

[`MeasurePacedBatchFanout.cs`](MeasurePacedBatchFanout.cs) measures the current public response API under paced
explicit submissions. Defaults are 20 items per submission, 30 items per downstream query, and simulated 20 ms I/O.
Configure `--submission-size`, `--downstream-batch-size`, and `--delay-ms` to change these independently; `--rate`
is submissions per second, not items per second. `--batch-size`, `--concurrency`, and `--pending-capacity` configure
RequestBatcher. Run with `--help` for all arguments, defaults, and bounds. It writes a JSON summary and per-submission samples, validates every response and exactly-once
processing, and reports arrival lateness alongside end-to-end latency. See the
[paced batch fan-out evaluation](../docs/evaluations/paced-batch-fanout/README.md) for matched configurations and results.

For example, compare both modes with identical rate, submission size, query size and delay:

```bash
dotnet run --configuration Release --file scripts/MeasurePacedBatchFanout.cs -- \
  --mode batcher --rate 500 --submission-size 20 --batch-size 120 --concurrency 2 \
  --downstream-batch-size 30 --delay-ms 20 --pending-capacity 65536 \
  --seconds 20 --warmup-seconds 3 --output /private/tmp/paced-fanout/custom.json
```

Use `--mode direct` for the baseline. Direct mode ignores Handler batch size, concurrency, and queue capacity;
it has no shared downstream limiter. Direct mode queries each submission independently, splitting it into parallel
calls only when it exceeds `--downstream-batch-size`; batcher mode can combine submissions before applying that same
query limit. A Handler that fits in one query directly awaits it, without `Task.WhenAll`. The delay is a fixed value
per simulated query, not a random latency distribution or a real downstream service. Each JSON records the selected
parameters. Per-phase limits are 500,000 submissions and 10,000,000 items; the phase timeout is its arrival duration
plus 120 seconds for draining. Overloaded configurations can therefore fail instead of completing indefinitely.

New probes can reuse [`SampleSummary.Calculate`](Common/SampleSummary.cs)
for count, mean, P50/P95/P99, and maximum values. Add `#:project Common/RequestBatcher.ScriptUtilities.csproj`
to a file-based script in this directory, as the fan-out probe does. The helper copies samples and uses nearest-rank percentiles (`ceil(N * p) - 1`), without
interpolation. Empty input has `Count = 0`; its zero-valued fields are not measured latency. Keep sample units and
timing boundaries in each workload; do not average percentiles from different runs as if they were a pooled percentile.

[`SummarizePartitionSubmissions.cs`](SummarizePartitionSubmissions.cs) validates and summarizes one independent
partition-count experiment. Run `dotnet run --file scripts/SummarizePartitionSubmissions.cs -- <benchmark-report.csv>
<benchmark.log> <output-directory>`. It requires six cases for one submission API and two successful process diagnostics
per case, checks request/batch counts and correctness flags, and writes `summary.csv` plus `diagnostics.jsonl`.
Run it separately for single and explicit submissions. See the [partition evaluation](../docs/evaluations/partition-submissions/README.md)
for commands, workload definitions and throughput units.

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
