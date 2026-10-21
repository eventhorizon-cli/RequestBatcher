# RequestBatcher tracing overhead

This evaluation measures the public RequestBatcher submission path with no tracing listener, a listener that samples
spans out, and a listener that records spans. It covers 256 logical integer requests with `BatchSize = 32` and
`MaxConcurrency = 1`, using one singleton handler and one unkeyed queue partition. The benchmark has two public
operations: 256 independent single submissions and eight explicit submissions containing 32 requests each.

The handler only increments a counter. After every measured wave, the benchmark verifies that the handler processed
exactly 256 requests in total. The validation is the same for every listener mode and is included in the measured
operation; it checks the total count rather than per-item identity. DI construction, input preparation, and provider
disposal are outside the measured operation. No exporter, processor, network, or external service is used.

## Results

Measured 2026-10-21 on macOS 27.0.1 Arm64, SDK 10.0.100, .NET 8.0.25, concurrent workstation GC, and
BenchmarkDotNet 0.15.8. Each case used two launches, five warmups, twelve measured iterations, and a 250 ms target.
Times and managed allocations are per 256-request wave; KB means 1,024 bytes. Error is the 99.9% confidence
interval margin reported by BenchmarkDotNet.

| Submission | Source | Listener mode | Mean | Error | Allocated |
| --- | --- | --- | ---: | ---: | ---: |
| 256 singles | RequestBatcher 0.2.4 package | Disabled | 117.57 us | 7.556 us | 87.45 KB |
| 256 singles | current source | Disabled | 119.07 us | 5.474 us | 87.84 KB |
| 256 singles | current source | Drop | 300.70 us | 6.985 us | 366.35 KB |
| 256 singles | current source | Recorded | 290.94 us | 4.263 us | 362.91 KB |
| 8 explicit groups | RequestBatcher 0.2.4 package | Disabled | 42.15 us | 1.040 us | 37.77 KB |
| 8 explicit groups | current source | Disabled | 43.94 us | 1.452 us | 37.71 KB |
| 8 explicit groups | current source | Drop | 82.37 us | 9.898 us | 63.56 KB |
| 8 explicit groups | current source | Recorded | 93.26 us | 8.906 us | 63.50 KB |

The package and current Disabled results overlap at the reported 99.9% intervals, so this run does not establish a
throughput regression or improvement in the uninstrumented path. The current source is about 1.3% slower for single
submissions and 4.2% slower for explicit groups in this sample, with the same allocation range. Those small
differences are below the confidence limits of this run.

Relative to current Disabled, current Drop adds about 181.63 us and 278.51 KB for single submissions, and about
38.43 us and 25.85 KB for explicit groups. Current Recorded adds about 171.87 us and 275.07 KB for single
submissions, and about 49.32 us and 25.79 KB for explicit groups. These are workload measurements for Activity
creation and completion handling. Recorded uses in-memory Activities only; there is no processor, exporter, or
telemetry storage in the measured process. The explicit-group tracing intervals are visibly multimodal, so the
incremental values should not be treated as a production latency guarantee.

The raw BenchmarkDotNet reports are retained as [current.csv](current.csv) and [baseline.csv](baseline.csv). Both
contain all six listener combinations; the table uses only the package Disabled rows as the old-version comparison.

The current library source was commit `969102cd853a237780c3744795b8ad55e978b674`. The package comparison
includes both context isolation and instrumentation changes; the current Disabled/Recorded comparison isolates
the incremental cost of enabling this instrumentation.

## Package evidence

The baseline project was restored from the default NuGet source with `PackageReference Include="RequestBatcher"
Version="0.2.4"`. Its assets file contains `RequestBatcher/0.2.4`, and the downloaded package was verified at:

```text
/private/tmp/requestbatcher-tracing-overhead/nuget-package-024/requestbatcher/0.2.4/requestbatcher.0.2.4.nupkg
SHA-512: 16784b16f17d1af2315161d0df1e81aedfb0a56bd1028fba49b5493050bed3a78f9da0681862bb8c1c3a0557fabe5c6943309f9e31ede8b96329b1f76099da32
```

## Observability

The optional `RequestBatcher.OpenTelemetry` package registers the core `RequestBatcher` ActivitySource with an
OpenTelemetry provider. The public `RequestBatcher.Execute` span measures the caller-visible submission, including
queue residence and handler completion. A dispatched `RequestBatcher.ProcessBatch` span represents handler execution
and uses links to the submission contexts that contributed requests. The batch span is created in an isolated
background context, so downstream database and HTTP instrumentation becomes its child without inheriting the first
web request's ambient trace. See the [observability design](../../observability-design.md) and the package
[instrumentation README](../../../src/RequestBatcher.OpenTelemetry/README.md).

## Method and reproduction

The benchmark source is [TracingOverheadBenchmarks](../../../tests/RequestBatcher.Benchmarks/InMemory/TracingOverheadBenchmarks.cs).
It listens only to the `RequestBatcher` ActivitySource. `Disabled` installs no listener, `Drop` returns
`PropagationData`, and `Recorded` returns `AllDataAndRecorded`; Recorded creates Activities in memory but installs no
processor or exporter. No `Task.Run` or additional benchmark worker is introduced.

The reproducible runner is [CompareTracingOverhead.cs](../../../scripts/CompareTracingOverhead.cs).
It creates a fresh run directory beneath the specified artifact directory, copies the benchmark project and pinned SDK configuration there, replaces only its core project reference with the published
`RequestBatcher 0.2.4` package reference, verifies `project.assets.json`, then runs the package and current projects
with the same source, options, runtime, job, and validation. It does not modify the Git worktree.

```bash
dotnet run --file scripts/CompareTracingOverhead.cs -- /private/tmp/requestbatcher-tracing-overhead
```

The runner uses two launches, five warmups, twelve measured iterations, and a 250 ms target. BenchmarkDotNet emitted
high-priority permission warnings in this environment and reported multimodal distributions for some cases; neither
changes correctness, but both increase uncertainty. Queue scheduling, thread-pool state, batch formation, asynchronous
completion, and benchmark coordination can dominate these small in-process waves. Allocations are managed allocations
reported by BenchmarkDotNet and include caller task aggregation and tracing state; they do not include native exporter
buffers or telemetry backend costs.
