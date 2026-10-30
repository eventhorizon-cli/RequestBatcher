# Performance Evaluation Index

Find reports by question; versions, workloads, and limitations are documented in each report's `README.md`. See the [Benchmark Index](../../tests/RequestBatcher.Benchmarks/README.md) for source and run commands.

## Batching, Concurrency, and Deduplication

- [deduplication-concurrency](deduplication-concurrency/README.md): compares batch size, concurrency, in-batch deduplication, `Task.WhenAll`, and submission size (simulated I/O).
- [partition-submissions](partition-submissions/README.md): independently measures single and explicit submission APIs with one/four partitions at fixed batch size and handler concurrency, including throughput, batch formation and duplicate-query work.
- [bufferqueue-2.1.0](bufferqueue-2.1.0/README.md): compares BufferQueue 2.0.3 and 2.1.0 with matched RequestBatcher source and the 20 ms tracing sampling matrix.

## Scheduling and External I/O

- [paced-batch-fanout](paced-batch-fanout/README.md): compares direct 20-item bulk queries with two Handler slots and batch size 120 under uniform explicit-submission arrivals, using simulated 20 ms I/O on .NET 10.
- [queue-pull-capacity](queue-pull-capacity/README.md): isolates prefilled queue draining and compares the complete pipeline with direct full batches for synchronous and 20 ms asynchronous handlers.
- [arrival-scheduling](arrival-scheduling/README.md): compares direct handlers with controlled `Task.Run` for async I/O, CPU, and synchronous blocking workloads (local PostgreSQL/Redis included).
- [external-io-scheduling](external-io-scheduling/README.md): evaluates `Task.Run`, handler concurrency, and connection count for real HTTP calls (local HTTP).
- [external-io-latency](external-io-latency/README.md): examines client time, server wait, connection pooling, and synchronous CPU prefixes (local HTTP).

## Public API Costs

- [named-batching](named-batching/README.md): compares named and unnamed batchers, single-item and explicit-list submission, and handler lifetimes (in-process).
- [delegate-handlers](delegate-handlers/README.md): compares delegate and typed handlers across service injection, return types, and batch configurations (in-process).
- [request-only delegate forwarding](delegate-handlers/request-only-forwarding/README.md): compares allocation before and after removing an async wrapper, including simulated 20 ms work.
- [batch scope cost](delegate-handlers/batch-scope/README.md): isolates scope creation/disposal and singleton service resolution, including the actual delegate forwarding path.

## Completion Paths and Allocation

- [typed-response-completion](typed-response-completion/README.md): measures typed completion objects on success, failure, and pre-dispatch cancellation paths (in-process).
- [completion-callback](completion-callback/README.md): measures coordinator completion callback reuse and pending-request delegate allocation (in-process).
- [shared-completion-source](shared-completion-source/README.md): measures shared completion objects and explicit-submission allocation (in-process).
- [pending-cancellation-token](pending-cancellation-token/README.md): measures allocation savings from removing the duplicate cancellation token field (in-process).
- [batch-error-aggregation](batch-error-aggregation/README.md): measures exception-reference reuse during batch failure fan-out and its lock, collection, and allocation costs (in-process).

## Observability

The [core histogram advice validation](core-histogram-advice/README.md) records container-owned metrics, direct Meter
aggregation, custom View precedence, and bounded .NET 10 measurements with the backlog gauge.

RequestBatcher exposes optional `RequestBatcher.Execute` and `RequestBatcher.Handle` spans through its
`RequestBatcher` ActivitySource. Each report identifies its measured version and listener/provider configuration;
historical baseline results do not describe the current implementation. Read the
[observability design](../observability-design.md) for correlation and configuration guidance.

The [observability-overhead evaluation](observability-overhead/README.md) measures disabled, metrics-only, and
tracing-plus-metrics OpenTelemetry providers around an asynchronous 20 ms pricing workload.

The [tracing evaluation guide](tracing-io-overhead/README.md) documents the current .NET 10 workloads, comparison
runner, and allocation tools.
The [single-partition .NET 10 investigation](tracing-io-overhead/single-partition-net10.md) records a current bounded
allocation probe and explains why explicit submissions still need multiple reverse links when split across handler batches.

The [metrics I/O evaluation](metrics-io-overhead/README.md) isolates SDK aggregation and periodic collection overhead
with tracing disabled, using the same 1,024-request waves and simulated 20 ms calls.

The [Execute and submission metrics evaluation](execute-metrics/README.md) compares the four-histogram implementation
against its previous metric contract on .NET 10, including public-call completion and submission-size recording.
The [shared call diagnostics validation](execute-metrics/call-diagnostics.md) covers the unified ordinary/delegate
boundary, both APIs through real OTLP export, and bounded .NET 10 metrics/tracing workloads.
