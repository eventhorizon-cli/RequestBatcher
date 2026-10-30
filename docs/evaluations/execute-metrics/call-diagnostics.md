# Shared call diagnostics validation

This is a historical validation of the implementation merged in `bd32da1` (#78). The shared diagnostics class described
below has since been removed; see the [current observability design](../../observability-design.md) for ownership and
call composition. These measurements describe the historical implementation only.

Validated on 2026-10-29 with SDK 10.0.100 and the actual .NET 10.0.0 runtime, macOS 27.2 Arm64.
The change routes ordinary and delegate submission adapters through `RequestBatcherCallDiagnostics`, preserving
delegate binding, cancellation, response assembly, and the existing completion helpers inside the call boundary.
This is a maintainability refactor; the measurements below do not establish a performance improvement.

## Correctness

- Unit suite: 561 tests passed on each of .NET 8 and .NET 10, including response and delegate single/group/mixed
  stress workloads with per-request results, response ordering, and exactly-once handling checks.
- The two Execute metrics test classes cover 55 cases per framework. Ordinary and delegate single/group,
  request-only/response entry points independently enable metrics and tracing and check exact emission counts.
  Binding failures remain synchronous and recorded; pre-canceled invalid delegates are never bound.
- All six integration tests passed on .NET 10: five in-process SDK stress cases and one live Testcontainers test.
  Live coverage exercises ordinary and delegate response groups separately, each with two bursts of 32 calls and
  364 total items. Each API must export exactly 64 Execute duration observations, 64 submission observations,
  matching item sums, one Execute span per caller, and correct bidirectional batch links and downstream spans.
  Containers, images, and the test network were removed after validation.
- Release solution build completed without warnings or errors. Formatting is restricted to changed C# files.

## Bounded hot-path measurements

Both existing workloads use 1,024 requests per wave, BatchSize 32, MaxConcurrency 8, and a 20 ms asynchronous
Handler delay. Single calls and explicit groups of 32 validate every request exactly once. Metrics workloads validate
all four exported histograms; periodic modes must collect before their final flush. Tracing workloads validate
their configured sampling and exported activities. These benchmarks use ordinary request-only handlers;
delegate correctness is covered above, not by these timing measurements.

| Workload | Configuration | Mean wave time | Allocated per wave |
| --- | --- | --- | --- |
| Single submissions | Metrics disabled | 104.04 ms | 284.92 KB |
| Single submissions | Metrics aggregation only | 105.14 ms | 526.67 KB |
| Explicit groups | Metrics disabled | 84.24 ms | 170.02 KB |
| Explicit groups | Metrics aggregation only | 84.64 ms | 177.44 KB |
| Single submissions | Tracing, 100% sampling | 98.38 ms | 1366.87 KB |
| Explicit groups | Tracing, 100% sampling | 84.46 ms | 240.17 KB |

All eight metrics and ten tracing cases completed successfully. Full results, including periodic metrics and other
sampling rates, are in [metrics CSV](call-diagnostics-metrics.csv) and [tracing CSV](call-diagnostics-tracing.csv).
The short runs, especially the three-iteration tracing run, have wide confidence intervals; they are bounded
validation measurements, not a before/after comparison or caller-latency estimate. Tracing group iterations were
below BenchmarkDotNet's recommended 100 ms minimum. No latency or allocation improvement is claimed.

## Commands

```bash
dotnet build RequestBatcher.slnx --configuration Release --no-restore
dotnet test tests/RequestBatcher.Tests/RequestBatcher.Tests.csproj --configuration Release --no-build --no-restore
REQUESTBATCHER_RUN_INTEGRATION_TESTS=1 dotnet test \
  tests/it/RequestBatcher.Observability.IntegrationTests/RequestBatcher.Observability.IntegrationTests.csproj \
  --configuration Release --no-build --no-restore --logger 'console;verbosity=normal'

NUGET_HTTP_CACHE_PATH=/private/tmp/requestbatcher-call-diagnostics-nuget \
dotnet run --project tests/RequestBatcher.Benchmarks/RequestBatcher.Benchmarks.csproj \
  --configuration Release --no-restore -- --filter '*TracingIoOverheadBenchmarks*' \
  --launchCount 1 --warmupCount 1 --iterationCount 3 --invocationCount 1 --unrollFactor 1 \
  --artifacts /private/tmp/requestbatcher-call-diagnostics-bdn

NUGET_HTTP_CACHE_PATH=/private/tmp/requestbatcher-call-diagnostics-nuget \
dotnet run --project tests/RequestBatcher.Benchmarks/RequestBatcher.Benchmarks.csproj \
  --configuration Release --no-restore -- --filter '*MetricsIoOverheadBenchmarks*' \
  --launchCount 1 --warmupCount 2 --iterationCount 5 --invocationCount 4 --unrollFactor 1 \
  --artifacts /private/tmp/requestbatcher-call-diagnostics-bdn
```

The first combined benchmark attempt used the shorter tracing settings for both classes. Its two Periodic1000
cases correctly failed validation because the run ended before a periodic export. Those metrics results were
discarded and all eight metrics cases rerun with the longer settings shown above. The writable NuGet cache avoids
the local sandbox's default-cache permission failure during BenchmarkDotNet's generated-project restore.
