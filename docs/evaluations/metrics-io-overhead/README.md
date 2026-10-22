# Metrics overhead with 20 ms downstream I/O

## Evaluation contract

Measure metrics independently of tracing using the current RequestBatcher source, .NET 8, and the real
OpenTelemetry SDK. No Activity listener is installed. Compare no meter subscription, SDK aggregation without
periodic collection, and periodic collection every 1,000 ms or 100 ms. The periodic modes use a validating in-process
exporter without retaining metric batches; transport, serialization, collectors, and backend costs are excluded.

## Results

Measured 2026-10-22 on Apple M2 Max, macOS 27.0.1, SDK 10.0.100, .NET 8.0.25, OpenTelemetry 1.20.1, and
BenchmarkDotNet 0.15.8. Production source matches `18f4422`. Values are per 1,024-request wave; KB is 1,024 bytes.
Error is the 99.9% confidence interval margin. Source and all raw data are committed alongside this report.

| Submission | Metrics mode | Mean | Error | Allocated | Allocation difference from disabled |
| --- | --- | ---: | ---: | ---: | ---: |
| Singles | Disabled | 102.68 ms | 3.482 ms | 289.34 KB | — |
| Singles | Aggregation only | 104.60 ms | 3.046 ms | 290.65 KB | +1.31 KB |
| Singles | Every 1,000 ms | 104.34 ms | 3.538 ms | 291.87 KB | +2.53 KB |
| Singles | Every 100 ms | 104.02 ms | 3.316 ms | 290.26 KB | +0.92 KB |
| Explicit groups | Disabled | 88.06 ms | 0.506 ms | 176.41 KB | — |
| Explicit groups | Aggregation only | 88.29 ms | 0.670 ms | 176.50 KB | +0.09 KB |
| Explicit groups | Every 1,000 ms | 87.94 ms | 0.631 ms | 176.20 KB | -0.21 KB |
| Explicit groups | Every 100 ms | 90.57 ms | 2.123 ms | 176.27 KB | -0.14 KB |

The single-submission allocation increase is approximately 0.3–0.9%, or 0.9–2.5 bytes per request. Explicit-group
differences are small and within batch-formation variability; negative differences do not mean metrics saves memory.
All corresponding elapsed-time confidence intervals overlap. The 100 ms explicit-group case has a higher mean and
more variable actual handler waits, so the data does not establish a consistent latency regression or an ordering of
collection intervals. Do not extrapolate the small differences into an exact per-instrument CPU cost.

Metrics allocation is much smaller than the tracing increases measured in the [tracing evaluation](../tracing-io-overhead/optimization.md).
The metrics implementation records numeric counters/histograms with a stack-local `TagList` and a value-type batch
scope; it does not create per-submission Activities or tracing completion tasks. This explains the different allocation
shape, without implying that SDK aggregation, synchronization, or export is free. Provider startup and initial storage
are outside the steady-state measurement; high-cardinality tags and real OTLP exporters are outside this workload.

All eight cases completed, with two launches and 69 validated waves per launch: 1,104 waves and 1,130,496 request
items, including warmup and diagnostic invocations. Aggregation-only cases had zero collections before cleanup.
The 1,000 ms cases collected 6–7 times before the final flush, and the 100 ms cases collected 64–76 times. All enabled
cases validated all five instruments, 70,656 submitted/processed items per launch, and matching histogram/batch counts.
Actual mean handler waits ranged from 21.767 to 22.351 ms. No Docker stack, allocation profiler, build, or unit-test
workload ran concurrently with the measured region.

Evidence: [raw results](results.csv), [validation output](validation.txt), and
[benchmark source](../../../tests/RequestBatcher.Benchmarks/InMemory/MetricsIoOverheadBenchmarks.cs).

## Workload

Use the same workload as the [tracing I/O evaluation](../tracing-io-overhead/README.md): 1,024 unique integers per
wave, batch size 32, handler concurrency 8, capacity 1,024, and a singleton handler awaiting a 20 ms delay per batch.
Compare single submissions with 32 explicit groups of 32. Use one named batcher to exercise both request-type and
batcher-name tags, identically across modes. Record actual handler delay and batch occupancy because timer scheduling
and batch formation can obscure small instrumentation costs.

## Internal flow and validation

Provider and batcher construction happen in global setup, outside measurement. Each measured wave submits all work,
awaits completion, and checks every request was processed exactly once. SDK aggregation runs during recording in every
enabled mode; only the periodic modes collect during measurement. Cumulative temporality makes a final forced
collection sufficient to validate submitted/processed request totals, processed batch totals, and histogram counts.
The final collection runs after draining the batcher, outside measurement. Validate that periodic collection actually
occurred before this final flush and that all five instruments were observed. Do not retain exported metric objects.

Use BenchmarkDotNet MemoryDiagnoser, two launches, four warmups, twelve measured iterations, and four waves per
iteration. Report elapsed time, its uncertainty, and managed allocation per 1,024-item wave. Allocation is cumulative,
not retained memory. Periodic collection happens on a background thread and can affect allocation totals and overlap
measured operations unevenly. Run without concurrent builds/tests or Docker workloads, and retain raw CSVs and
validation evidence. No production behavior, public API, or dependency changes are needed for this evaluation.

## Reproduction

```bash
dotnet run --project tests/RequestBatcher.Benchmarks/RequestBatcher.Benchmarks.csproj \
  --configuration Release -- --filter '*MetricsIoOverheadBenchmarks*' \
  --artifacts /private/tmp/requestbatcher-metrics-io
```
