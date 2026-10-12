# Changelog

## 0.0.8

- Require an explicit partition count with `UsePartitionKey(selector, partitionCount)`; the selector-only overloads
  are removed. Numeric and string keys require a partition
  count greater than one; equal keys retain partition locality, while partition count remains independent of handler
  concurrency and does not provide ordering guarantees.
- Clarify DI-owned lifecycle management. `StopAsync` is removed; `DisposeAsync` drains accepted work, and the
  application's service provider owns disposal when the batcher is registered with `AddRequestBatcher`.
- Ship both `net8.0` and `net10.0` package assets while retaining the repository SDK and C# language policy.
- Reduce synchronous dispatch overhead and per-item lifecycle-lock contention while preserving bounded handler
  concurrency, admission, failure, and drain behavior.
- Reduce measured allocation overhead by caching the coordinator completion callback (64 B per pending item), folding
  shared explicit completion into its TCS (24 B per group), and passing cancellation tokens through the registration
  callback (8 B per pending item). These Arm64 allocation measurements
  do not establish throughput gains. Exactly-once completion and cancellation semantics are preserved.
- Reduce typed-response single-submission allocations in the measured success, failure, and pre-dispatch cancellation
  scenarios, and add correctness coverage for routing, lifecycle, completion, response validation, and cancellation.
- Put DI registration and resolution first in the README and add benchmark and scheduling-probe coverage for the
  supported submission and routing paths.

### Migration

Register the handler and batcher in the application's existing `IServiceCollection`, resolve
`IRequestBatcher<TRequest>` (or `IRequestBatcher<TRequest, TResponse>`) from that application's provider, and let the
provider own disposal. Replace calls to `StopAsync` with asynchronous disposal of the owning service provider;
when using a host, let the host dispose its provider. For keyed routing, configure for example
`options.UsePartitionKey(request => request.OrderId, partitionCount: 4)`; use a count greater than one and do not rely
on key ordering or one handler invocation per key. Omit `UsePartitionKey` for one shared partition.

RequestBatcher remains in-process and does not recover pending work after a process failure.

## 0.0.7

- Upgrade BufferQueue from 2.0.2 to 2.0.3 for fair pull opportunities across readable
  partitions and routing that hashes the complete string key.
- Use one internal queue partition when `UsePartitionKey` is not configured, keeping
  queued requests together for batching. Keyed batchers retain
  `min(MaxConcurrency, max(1, Environment.ProcessorCount))` partitions.
- Document that partition count is independent of handler concurrency. A single
  partition can supply multiple concurrent asynchronous batches; equal keys do not
  guarantee one handler batch or ordered execution.
- Add regression coverage for queue routing, partition selection, unkeyed batching
  and concurrent execution from one partition, including request/response batching.
- Record scheduling and external-I/O evaluations for further discussion. Direct handler
  dispatch remains the default, with no new scheduling option or public partition setting.

Exact string-key partition assignments and handler batch composition may change after
this update. RequestBatcher remains in-process and does not recover pending work after
a process failure. The public API is unchanged.
