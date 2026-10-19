# Performance Evaluation Index

Find reports by question; versions, workloads, and limitations are documented in each report's `README.md`. See the [Benchmark Index](../../tests/RequestBatcher.Benchmarks/README.md) for source and run commands.

## Batching, Concurrency, and Deduplication

- [deduplication-concurrency](deduplication-concurrency/README.md): compares batch size, concurrency, in-batch deduplication, `Task.WhenAll`, and submission size (simulated I/O).
- [partition-key-submissions](partition-key-submissions/README.md): compares explicit batch submission with per-item submission followed by `Task.WhenAll` (in-process).
- [delay-overhead](delay-overhead/README.md): compares direct calls, direct batching, bounded batching, and RequestBatcher processing cost (simulated I/O).
- [bufferqueue-2.0.3](bufferqueue-2.0.3/README.md): measures how partition count, direct dispatch, and controlled `Task.Run` affect batch processing (in-process).

## Scheduling and External I/O

- [arrival-scheduling](arrival-scheduling/README.md): compares direct handlers with controlled `Task.Run` for async I/O, CPU, and synchronous blocking workloads (local PostgreSQL/Redis included).
- [external-io-scheduling](external-io-scheduling/README.md): evaluates `Task.Run`, handler concurrency, and connection count for real HTTP calls (local HTTP).
- [external-io-latency](external-io-latency/README.md): examines client time, server wait, connection pooling, and synchronous CPU prefixes (local HTTP).

## Public API Costs

- [named-batching](named-batching/README.md): compares named and unnamed batchers, single-item and explicit-list submission, and handler lifetimes (in-process).
- [delegate-handlers](delegate-handlers/README.md): compares delegate and typed handlers across service injection, return types, and batch configurations (in-process).

## Completion Paths and Allocation

- [typed-response-completion](typed-response-completion/README.md): measures typed completion objects on success, failure, and pre-dispatch cancellation paths (in-process).
- [completion-callback](completion-callback/README.md): measures coordinator completion callback reuse and pending-request delegate allocation (in-process).
- [shared-completion-source](shared-completion-source/README.md): measures shared completion objects and explicit-submission allocation (in-process).
- [pending-cancellation-token](pending-cancellation-token/README.md): measures allocation savings from removing the duplicate cancellation token field (in-process).
- [batch-error-aggregation](batch-error-aggregation/README.md): measures exception-reference reuse during batch failure fan-out and its lock, collection, and allocation costs (in-process).
