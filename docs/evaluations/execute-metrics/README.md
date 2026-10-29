# Execute and submission metrics on .NET 10

## Scope

This comparison measures the four-histogram contract: submission size, completed Handler batch size, Handler duration,
and public ExecuteAsync duration. It includes removal of the three redundant counters, the Handler-duration rename,
and size buckets derived from the largest registered BatchSize snapshot, capped at 25 buckets per size histogram.
The public-call completion wrapper is enabled only when
its histogram is subscribed. This is a functionality change with measured costs, not a performance optimization claim.

Baseline: `9d4726553beb9470a866cd9228a218602681a442`, with three counters and two batch histograms. Candidate: the
implementation accompanying this report. Both use OpenTelemetry 1.20.1 and the same existing
[metrics I/O workload](../../../tests/RequestBatcher.Benchmarks/InMemory/MetricsIoOverheadBenchmarks.cs).

Measured on 2026-10-29 using .NET 10.0.0, macOS 27.2.0, Arm64. The
[file-based probe](../../../scripts/MeasureMetricsOverhead.cs) runs five warmup waves followed by 30 measured waves.
Each wave has 1,024 unique integer requests, BatchSize 32, MaxConcurrency 8, queue capacity 1,024, one named pipeline,
and a Handler awaiting simulated 20 ms I/O. Compare 1,024 single calls with 32 collection calls of 32 items each.
There is no tracing, real downstream service, network export, or concurrent test/build workload during measurement.
The benchmark supplies its pipeline's sizing snapshot to its separately owned SDK container so the candidate's
automatic bucket views are exercised. Provider creation and final collection are outside the measured region.

## Results

Each range contains two separate process runs, not a confidence interval. Time is mean elapsed time per complete
1,024-item wave; it is not per-caller P95. Allocation is process-wide allocated bytes divided by measured item count,
not retained memory. Batch formation and timer scheduling introduce variation.

| Submission | Metrics | Baseline bytes/item | Candidate bytes/item | Baseline mean wave | Candidate mean wave |
| --- | --- | ---: | ---: | ---: | ---: |
| Single | Disabled | 283.60–286.41 | 283.36–283.69 | 103.24–108.04 ms | 102.56–103.43 ms |
| Single | SDK aggregation | 287.60–287.71 | 526.86–529.37 | 104.15–104.91 ms | 104.25–105.48 ms |
| Collection of 32 | Disabled | 168.75–169.24 | 170.24–172.75 | 85.04–85.62 ms | 85.02–85.06 ms |
| Collection of 32 | SDK aggregation | 169.98–172.93 | 177.23–177.27 | 84.71–85.70 ms | 84.98–86.37 ms |

With SDK aggregation enabled, single calls allocate approximately 239–242 additional bytes per item in paired runs.
The new public-call completion wrapper contributes per-call allocations. Collection calls share that work across
32 items; paired differences are approximately 4.3–7.3 bytes per item. These results measure the combined change,
not isolated costs of individual histograms, tags, or buckets. Disabled-mode differences are small but not uniformly
zero; this probe does not establish exact zero-overhead behavior.

The I/O-dominated timings do not establish a reliable latency improvement or an exact CPU overhead. There are only
two runs per case, and downstream delays can hide recording costs. High-cardinality tags, many distinct BatchSize
configurations, periodic collection, transport, and backend costs require separate evaluation.

## Validation and evidence

All 16 runs completed 35 validated waves each, including warmups: 573,440 request items. Every wave validates exactly-once
handling for every input ID. Enabled baseline runs validated five instruments; enabled candidate runs validated four
histograms. Each enabled run exported a cumulative size sum of 35,840 items. Candidate submission and Execute counts
were 35,840 for single calls or 1,120 for collection calls; batch-size and Handler-duration counts matched actual
Handler invocations. The aggregation-only reader performed no collection before cleanup.

The raw `baseline-*.json` and `candidate-*.json` files in this directory retain both process runs. See
[validation output](validation.txt) for instrument counts, measured Handler delays, and batch occupancy. Earlier cached
probe outputs were discarded; every retained run used `--no-cache` and checked its instrument count.

Correctness checks for the final implementation:

- Full unit suite: 541 passed on .NET 8 and 541 on .NET 10, including response stress with per-request correlation,
  collection response order, exactly-once handling, and enabled-metrics mixed submissions.
- Twelve real-SDK bucket tests cover small/large BatchSize, overflow, empty submissions, bounded bucket counts across 120 pipelines,
  registration order, provider isolation, repeated registration, and unchanged duration buckets.
- Five real-SDK tracing stress tests passed on .NET 10 after separating Span and Metrics tag constants.
- Sixteen delegate metrics tests cover all four submission shapes, tracing on/off, pending completion, binding failure,
  response order, and cancellation status/token preservation. The timed workload above uses ordinary Handler APIs;
  it does not isolate delegate binding or delegate dispatch costs.
- Live LGTM integration passed on .NET 10: 64 delegate collection calls and 364 request items were checked through the
  actual OTLP/Grafana/Prometheus path, including all four histograms and linked traces. All temporary containers were
  removed after the test. The sample dashboard uses the renamed histogram queries.

## Reproduction

Use separate processes and the same runtime for each source tree. Copy the probe into the baseline checkout's
`scripts/` directory; keep that checkout's production and benchmark source unchanged. Its `Common` helper project is
already present at the baseline commit. Run both submission kinds and both modes twice, reversing source order for
the second repetition:

```bash
dotnet run --no-cache --configuration Release --file scripts/MeasureMetricsOverhead.cs -- \
  AggregationOnly single /private/tmp/metrics/candidate-AggregationOnly-single-r1.json 30

dotnet run --no-cache --configuration Release --file scripts/MeasureMetricsOverhead.cs -- \
  Disabled multi /private/tmp/metrics/candidate-Disabled-multi-r1.json 30

dotnet test tests/RequestBatcher.Tests/RequestBatcher.Tests.csproj --configuration Release --no-restore

dotnet test tests/it/RequestBatcher.Observability.IntegrationTests/RequestBatcher.Observability.IntegrationTests.csproj \
  --configuration Release --no-restore --filter 'FullyQualifiedName~TracingStressTests'

REQUESTBATCHER_RUN_INTEGRATION_TESTS=1 dotnet test \
  tests/it/RequestBatcher.Observability.IntegrationTests/RequestBatcher.Observability.IntegrationTests.csproj \
  --configuration Release --no-restore --filter 'FullyQualifiedName~ObservabilityTests'
```

The probe reuses the benchmark workload directly; these are bounded diagnostics rather than BenchmarkDotNet
confidence-interval results. Use the benchmark index for the full periodic-reader benchmark matrix.

## Shared call boundary

The subsequent [shared call diagnostics validation](call-diagnostics.md) covers the unified ordinary/delegate wrapper,
independent metric/tracing switches, both APIs through live OTLP export, and bounded .NET 10 hot-path measurements.
