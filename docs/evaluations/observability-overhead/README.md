# RequestBatcher observability overhead

This evaluation compares the same named response batcher workload with no OpenTelemetry provider, the RequestBatcher
meter only, and both the RequestBatcher meter and ActivitySource registered with the OpenTelemetry SDK. It uses 256
individual requests, a batch size of 32, a maximum batch concurrency of 4, and a handler that asynchronously waits
20 ms before returning one response per request. The 20 ms wait represents the downstream pricing lookup in the sample;
the benchmark measures the complete caller-visible operation.

The benchmark validates every response on every measured wave. Service-provider creation and OpenTelemetry provider
construction and disposal are outside the measured operation. The metrics and tracing providers use the OpenTelemetry
InMemory exporter with a non-retaining counting collection, so no exporter network, external service, or unbounded
telemetry list is included. The tracing-plus-metrics provider is the real in-process OpenTelemetry SDK provider; the
disabled case has no provider listener at all. Cleanup verifies that both enabled providers received telemetry.

## Results

Measured 2026-10-22 on macOS 27.0.1 Arm64, SDK 10.0.100, .NET 8.0.25, concurrent workstation GC, and
BenchmarkDotNet 0.15.8. Each case used two launches, five warmups, twelve measured iterations, and a 500 ms target.
Times and managed allocations are per 256-request wave. Error is the 99.9% confidence interval margin reported by
BenchmarkDotNet.

| Provider mode | Mean | Error | Allocated | Difference from disabled |
| --- | ---: | ---: | ---: | ---: |
| Disabled | 60.85 ms | 1.70 ms | 90.31 KB | baseline |
| Metrics | 62.35 ms | 1.06 ms | 91.42 KB | +1.50 ms, +1.11 KB |
| Tracing + metrics | 62.26 ms | 1.13 ms | 430.54 KB | +1.41 ms, +340.23 KB |

The disabled and metrics-only intervals overlap at their reported 99.9% confidence margins, so this run does not
establish a meaningful latency regression from metrics registration for this workload. Tracing plus metrics also falls
within the disabled interval margin in this run, while adding about 340 KB of managed allocation per 256-request wave,
or about 1.3 KB per request. Each handler waits 20 ms, and processing the whole wave needs multiple batches under the
concurrency limit. Downstream waiting dominates the wave duration; the allocation impact should still be considered when enabling recorded tracing at high
request rates.

The benchmark exports to a non-retaining in-process collection. Exporter network cost, backend ingestion, and remote
collector behavior are outside these measurements. Sampling policy, batch export queues, scheduling, and timer variance
also affect the small latency differences. Repeat the benchmark on the target runtime and hardware before using the
numbers as a capacity limit.

## Method and reproduction

The benchmark source is [ObservabilityOverheadBenchmarks](../../../tests/RequestBatcher.Benchmarks/InMemory/ObservabilityOverheadBenchmarks.cs).
The named pipeline is `pricing-queries`, matching the observability sample. The handler waits asynchronously for 20 ms,
then sets each response to the original request value. This keeps the downstream work realistic enough to expose the
relative instrumentation cost while avoiding Docker or database noise. The exporter collections count received records
without retaining them, which avoids making the benchmark's own telemetry buffer part of the allocation result.

Run the benchmark from the repository root:

```bash
dotnet run --project tests/RequestBatcher.Benchmarks/RequestBatcher.Benchmarks.csproj \
  --configuration Release -- \
  --filter '*ObservabilityOverheadBenchmarks*' \
  --artifacts /private/tmp/requestbatcher-observability-overhead
```

BenchmarkDotNet may report a high-priority permission warning on macOS. That warning does not change the response
validation, but it increases timing variance. The recorded run also showed a multimodal disabled distribution, so the
confidence intervals should be read as workload evidence rather than a production latency guarantee.
