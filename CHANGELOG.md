# Changelog

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
