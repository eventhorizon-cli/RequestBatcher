# Core histogram advice validation

This validates the container-owned core metrics service and migration of size-histogram boundaries from OpenTelemetry
views to `InstrumentAdvice<long>`. It includes the existing backlog gauge changes and BufferQueue 2.2.0 in the working
tree. This is a functionality validation, not a before/after performance comparison or optimization claim.

## Environment and workload

Measured on 2026-10-30 with SDK 10.0.100, .NET 10.0.0, macOS 27.2.0, Arm64, Release configuration, and
OpenTelemetry 1.20.1. Each case uses five warmup waves followed by 30 measured waves of 1,024 unique requests,
BatchSize 32, concurrency eight, and simulated 20 ms Handler I/O. Single submissions and explicit groups of 32 are
measured in separate processes with tracing disabled. The enabled mode aggregates with the SDK and collects at cleanup;
it does not measure periodic collection, OTLP transport, or backend costs. No build or test ran alongside measured waves.

| Submission | Metrics | Mean per wave (ms) | Allocated bytes per item |
| --- | --- | ---: | ---: |
| Single | Disabled | 104.566 | 282.830 |
| Single | Aggregation only | 104.854 | 538.394 |
| Explicit groups | Disabled | 84.963 | 169.120 |
| Explicit groups | Aggregation only | 85.230 | 177.714 |

These are bounded current-source measurements, including runtime scheduling and batch-formation variation. They do
not isolate the cost of moving bucket configuration into core. Container startup and bucket calculation occur outside
the measured region. JSON files retain runtime, allocation, and wave-duration summaries; [validation.txt](validation.txt)
retains correctness and exported totals.

## Correctness

All four cases validated each request's exactly-once handling across 35 waves (35,840 items per case). Enabled cases
exported all four histograms plus the backlog gauge. Submission and batch sums each equaled 35,840; submission and
Execute counts equaled 35,840 for singles and 1,120 for explicit groups. Handler duration counts matched actual batch
counts. The final sampled backlog was zero.

Unit coverage checks small and maximum integer BatchSize, overflow, native Advice visibility, direct SDK aggregation,
explicit View precedence, independent container Advice and disposal, and all registration shapes. Public API integration
coverage combines ordinary and delegate response pipelines with BatchSize 40/120 and verifies response order,
exactly-once handling, raw measurements, exported boundaries, bucket counts, sums, and tags.

Final validation passed 578 unit tests on each of net8.0 and net10.0, plus six .NET 10 Advice/tracing integration
cases without Docker. The scoped-response tracing test now validates every actual Handler batch rather than assuming
one explicit submission always forms one batch. Solution build completed without warnings or errors; formatting and
package checks passed. The core package declares DiagnosticSource directly only for net8.0.

## Commands

Run from the repository root; repeat the measurement command for `Disabled`/`AggregationOnly` and `single`/`multi`,
choosing a separate output path for each case. Subsequent runs may use `--no-build` instead of `--no-cache`.

```bash
dotnet run --configuration Release --file scripts/MeasureMetricsOverhead.cs --no-cache \
  --disable-build-servers --artifacts-path /tmp/requestbatcher-advice-build -- \
  Disabled single docs/evaluations/core-histogram-advice/disabled-single.json 30
dotnet test tests/RequestBatcher.Tests/RequestBatcher.Tests.csproj --configuration Release
dotnet test tests/it/RequestBatcher.Observability.IntegrationTests/RequestBatcher.Observability.IntegrationTests.csproj \
  --configuration Release --filter 'FullyQualifiedName~MetricsAdviceTests|FullyQualifiedName~TracingStressTests'
```
