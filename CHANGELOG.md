# Changelog

All notable changes to RequestBatcher are documented in this file.

## [Unreleased]

### Changed

- Reduced explicit batch completion allocations by sharing one completion state across the submission while preserving handler failures, cancellation, and all-request completion semantics.
- Redesigned dispatch so `MaxConcurrency` limits handler batches while internal queue partitions are capped at the
  process-visible logical-core count. Pulled batches auto-commit before handler execution, so `MaxPendingRequests`
  bounds queue-resident requests only.
- Removed global, partition-local, and partition-key handler execution ordering guarantees. `UsePartitionKey` remains
  a routing feature only; equal-key batches can execute concurrently. This is a behavioral compatibility change.

### Added

- Added first-class request/response batching through `IRequestBatcher<TRequest, TResponse>` and
  `IRequestBatchHandler<TRequest, TResponse>`. Response batches use `RequestBatchItem<TRequest, TResponse>` and can
  assign responses directly or map the nth response in an ordered enumeration to the nth request item with `SetResponses`.

## [0.0.1] - 2026-08-15

First public release.

### Added

- Single-request and explicit batch submission through `IRequestBatcher<TRequest>`.
- Opportunistic batching with bounded concurrency and configurable backpressure.
- Optional numeric and string partition keys with partition-local ordering.
- Per-request completion, handler exception propagation, caller cancellation, and graceful shutdown.
- Dependency injection integration with explicit handler lifetimes and `Microsoft.Extensions.Logging` support.
- PostgreSQL and Redis benchmarks, plus a PostgreSQL Web API sample for batched writes and deduplicated reads.

[Unreleased]: https://github.com/eventhorizon-cli/RequestBatcher/compare/v0.0.1...HEAD
[0.0.1]: https://github.com/eventhorizon-cli/RequestBatcher/releases/tag/v0.0.1
