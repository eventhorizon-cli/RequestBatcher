# Tracing evaluation guide

The [single-partition .NET 10 investigation](single-partition-net10.md) records the current bounded allocation probe,
its raw output, and the decision to retain the existing reverse-link representation. It is not a before/after
optimization comparison. The [observability design](../../observability-design.md) defines correlation, sampling,
and completion contracts.

## Workloads

[TracingIoOverheadBenchmarks](../../../tests/RequestBatcher.Benchmarks/InMemory/TracingIoOverheadBenchmarks.cs) submits
1,024 unique requests per wave, individually or as 32 explicit groups of 32. It uses one unkeyed partition,
`BatchSize = 32`, `MaxConcurrency = 8`, `MaxPendingRequests = 1,024`, and a singleton handler awaiting a simulated
20 ms delay per batch. Each wave validates every request ID exactly once. Sampling decisions must match completed
sampled spans. There is no exporter, metrics provider, or ambient caller parent.

The SDK registers the RequestBatcher source directly with `ParentBasedSampler(new TraceIdRatioBasedSampler(ratio))`
at 0%, 1%, 10%, or 100%, plus a separate disabled-provider case. This does not exercise the optional linked-batch
sampler. Explicit submissions share Execute state across their items, but can still span several Handler batches.

All current script entry points and generated evaluation projects target .NET 10. Record the actual runtime,
configuration, source revisions, workload, validation output, and time/allocation results for each comparison.
Do not infer a runtime from the selected SDK alone. Run measurements sequentially without concurrent local builds,
tests, or benchmarks. Allocation probes do not establish latency or throughput gains.

## Source comparisons

Use a baseline checkout containing the production source to compare with the current tree:

```bash
dotnet run --file scripts/CompareTracingIoOverhead.cs -- \
  --compare-source --baseline-source /private/tmp/requestbatcher-tracing-baseline \
  /private/tmp/requestbatcher-tracing-comparison
```

Both source snapshots and generated BenchmarkDotNet artifacts are retained under a unique `run-*` directory.
`--baseline-source` requires `--compare-source`. Without a baseline source, source comparison uses published 0.3.2;
`--include-legacy` optionally adds the non-tracing 0.2.4 cases. Without `--compare-source`, the runner compares the
published 0.2.4 and 0.3.2 packages. Package references and hashes are checked by the runner.

For the lightweight-handler package comparison, use:

```bash
dotnet run --file scripts/CompareTracingOverhead.cs -- /private/tmp/requestbatcher-tracing-overhead
```

This compares published 0.2.4 with current source. `--prepare-only` restores and verifies its baseline without running
the benchmark; `TRACING_OVERHEAD_FILTER` selects cases. These package comparisons do not isolate one source change.
See the [script index](../../../scripts/README.md) for entry points and arguments.

## Allocation diagnostics

Run the current single-submission probe in separate processes. It performs ten warmup waves before measuring the
requested number of waves; cleanup diagnostics include warmup, while the allocation window excludes it.

```bash
dotnet run --file scripts/ProfileTracingAllocations.cs --configuration Release -- Disabled 30
dotnet run --file scripts/ProfileTracingAllocations.cs --configuration Release -- Sample0 30
dotnet run --file scripts/ProfileTracingAllocations.cs --configuration Release -- Sample100 30
```

For allocation attribution, use Microsoft's [dotnet-trace tool](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/dotnet-trace).
Build the probe before collecting a trace so the captured process runs the workload without a build:

```bash
dotnet build scripts/ProfileTracingAllocations.cs --configuration Release \
  --output /private/tmp/requestbatcher-allocation-probe
dotnet-trace collect --profile gc-verbose --providers RequestBatcher-AllocationProbe \
  --output /private/tmp/requestbatcher-sample0.nettrace -- \
  dotnet /private/tmp/requestbatcher-allocation-probe/ProfileTracingAllocations.dll Sample0 500
dotnet run --file scripts/AnalyzeAllocationTrace.cs -- \
  /private/tmp/requestbatcher-sample0.nettrace /private/tmp/requestbatcher-sample0
```

The analyzer limits allocation samples to the probe's marker window and writes `.types.csv`, `.stacks.csv`, and
`.summary.txt`. Allocation ticks estimate byte distribution; they are not exact object counts. Check lost events and
missing stacks before interpreting them. Tracing can change scheduling and batch formation, so captured timings are
not latency benchmark results. Activities, Tasks, and shared submission contexts must retain their lifetime and
completion contracts when evaluating allocation changes.
