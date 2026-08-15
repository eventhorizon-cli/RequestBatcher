# Changelog

All notable changes to RequestBatcher are documented in this file.

## [1.0.0] - 2026-08-15

First public release.

### Added

- Single-request and explicit batch submission through `IRequestBatcher<TRequest>`.
- Opportunistic batching with bounded concurrency and configurable backpressure.
- Optional numeric and string partition keys with partition-local ordering.
- Per-request completion, handler exception propagation, caller cancellation, and graceful shutdown.
- Dependency injection integration with explicit handler lifetimes and `Microsoft.Extensions.Logging` support.
- PostgreSQL and Redis benchmarks, plus a PostgreSQL Web API sample for batched writes and deduplicated reads.

[1.0.0]: https://github.com/eventhorizon-cli/RequestBatcher/releases/tag/v1.0.0
