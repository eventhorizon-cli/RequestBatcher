# Single-partition tracing investigation on .NET 10

The current implementation still incurs tracing allocation with one partition. Partition count alone does not justify
removing trace links, submission counting, or completion synchronization. Retain the existing implementation: its
first reverse link already avoids a list allocation, and a partition-specific path would add complexity without a
proven single-handler-batch guarantee. This investigation records an allocation baseline, not an optimization gain.

## Measurement

Measured on 2026-10-28 (Asia/Tokyo), macOS 27.2 Arm64, .NET SDK 10.0.100 and Microsoft.NETCore.App 10.0.0,
using Release configuration. The generated probe runtime configuration targets `net10.0` and framework `10.0.0`;
`dotnet --info` confirms 10.0.0 is the installed .NET 10 runtime. OpenTelemetry is 1.20.1.
Measured source commit: `0c6300370506c37c1fcd442cc6be0e0b81b316cc`; its production code is unchanged in merged
commit `b9af446c5d4d3fbf5b4f421b4a96e1da71d19071`.

The existing [allocation probe](../../../scripts/ProfileTracingAllocations.cs) runs
[TracingIoOverheadBenchmarks.SingleSubmissionsAsync](../../../tests/RequestBatcher.Benchmarks/InMemory/TracingIoOverheadBenchmarks.cs).
Each wave submits 1,024 requests individually to one unkeyed partition, with `BatchSize = 32`, `MaxConcurrency = 8`,
and `MaxPendingRequests = 1,024`. The singleton handler awaits `Task.Delay(20)` per batch.

Each mode runs sequentially in a separate process, with ten warmup waves followed by 30 measured waves (30,720
measured requests). Process-wide managed allocation is measured with `GC.GetTotalAllocatedBytes(precise: true)`.
The window includes caller aggregation and validation; these are not isolated Activity object costs.
There is no ambient parent, exporter, metrics provider, or retained telemetry. Enabled modes register the source
directly with `ParentBasedSampler(new TraceIdRatioBasedSampler(ratio))`, not the optional linked-batch sampler.

| Mode | Allocated bytes, 30 waves | Bytes per request | Additional bytes per request vs. disabled |
| --- | ---: | ---: | ---: |
| Disabled | 8,692,248 | 282.95 | — |
| 0% sampling | 30,212,728 | 983.49 | 700.54 |
| 100% sampling | 40,324,280 | 1,312.64 | 1,029.69 |

All three runs passed the workload's per-ID exactly-once handling checks. The 0% run recorded zero sampled spans;
the 100% run completed all 42,417 sampled decisions. Sampling totals and cleanup timing diagnostics include warmup
(40 waves), while allocation totals cover only the 30 measured waves. See the retained
[probe output](single-partition-net10-output.txt).

These are bounded diagnostic runs, with one process per mode and no confidence intervals, not a full BenchmarkDotNet
comparison. They do not measure explicit-group or typed-response submissions, low arrival rates, tail latency, or
exporter costs. Batch formation varies between modes. The cleanup timings are diagnostic only and do not establish
a latency regression or gain. This report provides no matched baseline for a runtime or implementation improvement.

## Source findings

- [RequestBatcherTracing.StartBatch](../../../src/RequestBatcher/Diagnostics/RequestBatcherTracing.cs) already skips
  link collection when no participating submission is sampled. At 0%, the remaining cost therefore cannot all be
  attributed to link deduplication. Activity lifetime, context propagation, completion notifications, and tags still
  contribute. A propagation-only Activity is compatible with a non-recorded sampling result; see the
  [.NET sampling contract](https://learn.microsoft.com/en-us/dotnet/core/compatibility/core-libraries/10.0/activity-sampling).
- [BatchTraceLinks](../../../src/RequestBatcher/Diagnostics/BatchTraceLinks.cs) already stores the first context
  without a dictionary. Additional distinct contexts use a lazily allocated dictionary. A batch entirely from one
  explicit submission therefore already avoids that dictionary, but still examines each item.
- `CountSubmissions` reuses an existing link collector's count when its unique trace/span-pair count equals the item
  count. Otherwise it scans completion
  identities and may allocate a `HashSet<Task>`. Context count alone is insufficient: separate calls can share the
  same fallback parent context, and requests without contexts must still be counted.
- `StartBatch` creates an `ActivityTagsCollection` for every batch with listeners and computes a named Handler span
  name each time. Execute already caches immutable name/type metadata. Batch metadata preparation is a candidate
  for similar reuse, while retaining creation-time sampler inputs and per-batch size.
- [SubmissionTraceContext](../../../src/RequestBatcher/Diagnostics/SubmissionTraceContext.cs) synchronizes batch-link
  collection with completion. One partition still permits multiple concurrent batches and one submission can span
  those batches. Partition count is not sufficient proof that this state can be unsynchronized.

## Explicit submission and reverse links

An Execute span links back to each Handle span that processed its requests. Multiple partitions can split one
explicit submission into several handler batches and therefore several reverse links. One partition removes that
routing split, but it does not preserve a submission as one handler batch: each pull takes at most `BatchSize`
currently queued requests, and its boundaries need not match caller groups.

When only one handler batch processes a recorded submission, `RecordedSubmissionTraceContext` already stores its
context inline in `_firstBatch`. `_additionalBatches` is allocated only when another handler batch is linked.
Single-partition submissions that remain in one handler batch already benefit from this representation.

Keep this implementation and its synchronization. A single partition can supply concurrent batches, while completion
can race with dispatch; partition count cannot justify a lock-free single-link representation. No additional special
case is planned from this investigation. The measured probe uses single requests, so it does not quantify the
reverse-link allocation difference for explicit submissions across one and multiple partitions. Current tests demonstrate
[splitting and concurrent batches](../../../tests/RequestBatcher.Tests/Diagnostics/RequestBatcherTracingTests.cs),
[mixed sampling and submission counts](../../../tests/RequestBatcher.Tests/Diagnostics/TracingSamplingContractTests.cs),
and [single-partition merging](../../../tests/RequestBatcher.Tests/Batching/UnkeyedPartitionTests.cs).

## Reproduction

Run sequentially from the repository root; do not run other local builds or benchmarks during measurement:

```bash
dotnet --info
dotnet run --file scripts/ProfileTracingAllocations.cs --configuration Release -- Disabled 30
dotnet run --file scripts/ProfileTracingAllocations.cs --configuration Release -- Sample0 30
dotnet run --file scripts/ProfileTracingAllocations.cs --configuration Release -- Sample100 30
```

No production code, benchmark workload, or script entry point changed for this investigation.
