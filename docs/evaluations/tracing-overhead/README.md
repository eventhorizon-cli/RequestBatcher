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
BenchmarkDotNet 0.15.8. Each case used one launch, three warmups, eight measured iterations, and a 150 ms target.
Times and managed allocations are per 256-request wave; KB means 1,024 bytes.

| Submission | Source | Listener mode | Mean | 99.9% error | Allocated |
| --- | --- | --- | ---: | ---: | ---: |
| 256 singles | `7800065` baseline | Disabled | 144.19 us | 27.75 us | 89.54 KB |
| 256 singles | current | Disabled | 120.02 us | 63.37 us | 95.34 KB |
| 256 singles | current | Drop | 384.81 us | 209.46 us | 369.89 KB |
| 256 singles | current | Recorded | 274.13 us | 11.89 us | 365.29 KB |
| 8 explicit groups | `7800065` baseline | Disabled | 53.02 us | 11.18 us | 37.65 KB |
| 8 explicit groups | current | Disabled | 45.96 us | 13.83 us | 37.58 KB |
| 8 explicit groups | current | Drop | 89.16 us | 23.29 us | 63.50 KB |
| 8 explicit groups | current | Recorded | 92.03 us | 19.73 us | 63.46 KB |

The disabled baseline/current timing intervals overlap, so this run does not establish a throughput regression or
improvement in the uninstrumented path. Current recorded tracing allocated about 3.8x the disabled allocation for
single submissions and about 1.7x for explicit groups in this workload. The sampled-out listener still exercises
context capture and completion handling, and its result was noisy; it should be treated as a workload measurement,
not a universal cost estimate. Recorded single submissions were more stable than the other cases, but this is one
launch on one machine and does not establish a production latency guarantee.

The baseline's `Drop` and `Recorded` rows are omitted because commit `780006523107423d3aa47bb0356dac3d0ed13847`
contains no RequestBatcher tracing source or spans; installing the benchmark listener there is only a control for
listener setup and cannot measure instrumentation behavior. The current rows measure the tracing implementation committed as `179ac45`, before the
optional `requestbatcher.name` tag was added. All measured registrations are unnamed; named
registration tagging has not been benchmarked. Both snapshots use the same benchmark source, options, request count, handler, and BenchmarkDotNet job.

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

The baseline was created with `git archive` from commit
`780006523107423d3aa47bb0356dac3d0ed13847` into `/private/tmp/requestbatcher-baseline`, then the identical benchmark
source was copied into that snapshot. The raw reports are retained as [current.csv](current.csv) and [baseline.csv](baseline.csv).

Run the current benchmark from the repository root:

```bash
NUGET_PACKAGES=/private/tmp/requestbatcher-nuget-current NuGetAudit=false \
dotnet run --project tests/RequestBatcher.Benchmarks/RequestBatcher.Benchmarks.csproj \
  --configuration Release -- \
  --filter '*TracingOverheadBenchmarks*' \
  --artifacts /private/tmp/requestbatcher-tracing-current
```

Run the matched baseline from its archived snapshot after copying the same benchmark source:

```bash
NUGET_PACKAGES=/private/tmp/requestbatcher-nuget-baseline NuGetAudit=false \
dotnet run --project /private/tmp/requestbatcher-baseline/tests/RequestBatcher.Benchmarks/RequestBatcher.Benchmarks.csproj \
  --configuration Release -- \
  --filter '*TracingOverheadBenchmarks*' \
  --artifacts /private/tmp/requestbatcher-tracing-baseline
```

These are short, gated in-process waves rather than sustained service throughput or tail-latency tests. Queue
scheduling, thread-pool state, batch formation, asynchronous completion, and benchmark coordination can dominate
small cases. One launch per case and overlapping confidence intervals limit the strength of timing conclusions.
Allocations are managed allocations reported by BenchmarkDotNet and include caller task aggregation and tracing state;
they do not include native exporter buffers or telemetry backend costs.
