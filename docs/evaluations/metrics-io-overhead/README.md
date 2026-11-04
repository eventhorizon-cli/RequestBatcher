# Metrics overhead with downstream I/O

Historical evaluation of the five-instrument metrics contract at source `18f4422`, using .NET 8.0.25, OpenTelemetry 1.20.1, and BenchmarkDotNet 0.15.8 on an Apple M2 Max (2026-10-22). It compared disabled metrics, SDK aggregation without periodic collection, and collection every 1,000 or 100 ms. Each wave submitted 1,024 requests through either singles or explicit groups of 32; handlers awaited 20 ms. No Activity listener was installed.

Across the four single-submission modes, mean times were 102.68–104.60 ms and allocations 289.34–291.87 KB per wave. Explicit-group times were 87.94–90.57 ms and allocations 176.20–176.50 KB. Timing intervals overlapped, and the small allocation differences were not consistent enough to establish an interval ordering. The observed single-request allocation increase was 0.9–2.5 KB per 1,024-item wave. These results do not isolate per-instrument CPU cost or account for exporters, transport, collectors, or backend ingestion.

The benchmark validated all five instruments and request totals across two launches. Reproduce with [MetricsIoOverheadBenchmarks.cs](../../../tests/RequestBatcher.Benchmarks/InMemory/MetricsIoOverheadBenchmarks.cs). The [observability design](../../observability-design.md) defines the current metrics contract; the historical totals above do not describe its changed instrument set.
