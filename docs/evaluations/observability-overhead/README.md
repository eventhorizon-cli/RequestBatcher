# RequestBatcher observability overhead

Historical comparison of a named response batcher with no provider, metrics only, and OpenTelemetry tracing plus metrics. Measured 2026-10-22 on an Apple M2 Max with SDK 10.0.100, .NET 8.0.25, and BenchmarkDotNet 0.15.8. Each measured wave submitted 256 requests; handlers ran with batch size 32, concurrency 4, and an asynchronous 20 ms wait. In-memory exporters counted records without retaining them.

Mean wave times were 60.85 ms disabled, 62.35 ms with metrics, and 62.26 ms with tracing plus metrics; the reported 99.9% confidence margins overlap. Allocations were 90.31 KB, 91.42 KB, and 430.54 KB, respectively. Thus this run showed no clear latency change but recorded tracing increased managed allocation by about 340 KB per wave (about 1.3 KB per request). This workload excludes exporter transport, backend ingestion, and production sampling/export queues; results are not a capacity limit.

Every wave validated responses and cleanup verified telemetry delivery. Reproduce with [ObservabilityOverheadBenchmarks.cs](../../../tests/RequestBatcher.Benchmarks/InMemory/ObservabilityOverheadBenchmarks.cs). Measurements describe this simulated downstream workload and historical runtime only.
