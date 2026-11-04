# Per-batch dependency-injection scope cost

This historical .NET 8.0.25 / Arm64 macOS benchmark measured the existing delegate Handler scope boundary for singleton injection. One operation represented one Handler batch; provider creation and warmup were excluded, while resolution, transformation, and validation were included. It did not measure async disposal, queueing, contention, or end-to-end I/O.

The matched root-resolution control took 16.93 ns and allocated 0 B; creating and disposing a scope with singleton resolution took 41.80 ns and allocated 128 B. The actual delegate path took 47.41 ns and also allocated 128 B. The 128 B scope cost is per batch, or 4 B/request in a full batch of 32, but 128 B/request for a batch of one. This isolated result does not establish an end-to-end latency or GC benefit from avoiding singleton-only scopes.

Source and reproduction: [BatchScopeBenchmarks](../../../../tests/RequestBatcher.Benchmarks/InMemory/BatchScopeBenchmarks.cs). The current scope contract is in the [delegate design](../../../delegate-handlers-design.md).
