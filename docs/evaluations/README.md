# Performance Evaluation Index

Keep concise experimental conclusions and reproduction guidance here. Bulk CSV/JSON exports, per-run tables,
hash manifests, logs, and traces belong in local output directories. Do not retain obsolete dependency comparisons
or one-time optimization and validation reports. Preserve reusable workloads and correctness tests.

These summaries identify historical measurement conditions; they do not establish guarantees for other machines
or current releases. See the [benchmark index](../../tests/RequestBatcher.Benchmarks/README.md) and
[script index](../../scripts/README.md) for reusable source and run commands.

## Batching and public APIs

- [Deduplication and concurrency](deduplication-concurrency/README.md): batch size, concurrency, `Task.WhenAll`, and submission size with simulated I/O.
- [Partition submissions](partition-submissions/README.md): independent single/explicit submission experiments showing fragmentation and key-locality tradeoffs.
- [Named batching](named-batching/README.md): named/unnamed lookup, submission APIs, and handler lifetimes.
- [Delegate handlers](delegate-handlers/README.md): typed/delegate handler costs and service injection.
- [Batch scope cost](delegate-handlers/batch-scope/README.md): scope creation, disposal, and singleton service resolution.

## Scheduling and external I/O

- [Paced batch fan-out](paced-batch-fanout/README.md): direct bulk queries versus batching under uniform explicit arrivals and simulated I/O.
- [Queue pull capacity](queue-pull-capacity/README.md): prefilled queue draining and pipeline capacity with synchronous/asynchronous handlers.
- [Arrival scheduling](arrival-scheduling/README.md): direct dispatch versus bounded offloading for asynchronous, CPU, and blocking work.
- [External I/O scheduling](external-io-scheduling/README.md): offloading, handler concurrency, and connection count with local HTTP.
- [External I/O latency](external-io-latency/README.md): server waits, connection pooling, and synchronous CPU prefixes with local HTTP.

## Allocation and observability

- [Response hot paths](response-hot-paths/README.md): response allocation, temporary-array pooling, and timing limitations.
- [Observability overhead](observability-overhead/README.md): disabled, metrics-only, and tracing-plus-metrics providers with simulated I/O.
- [Metrics I/O overhead](metrics-io-overhead/README.md): SDK aggregation and periodic collection with tracing disabled.
- [Tracing guide and conclusions](tracing-io-overhead/README.md): tracing workloads, source comparison, allocation tools, and single-partition interpretation.

The [observability design](../observability-design.md) defines the current correlation and configuration contracts.
