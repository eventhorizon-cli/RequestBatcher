# Tracing allocation profile

## Method

This diagnostic separates allocation sources for the optimized source measured in [the allocation comparison](optimization.md).
It reuses `TracingIoOverheadBenchmarks` with tracing disabled, SDK sampling at 0%, or SDK sampling at 100%.
Each mode runs in its own .NET 8 process with ten warmup waves, then 500 measured waves of 1,024 single submissions.
Handler batches await a simulated 20 ms downstream call. Metrics and exporters are disabled. Every wave validates
that each request was processed exactly once.

`ProfileTracingAllocations.cs` emits an EventSource marker window around only the 500 measured waves and reports
the process-wide allocated-byte delta using `GC.GetTotalAllocatedBytes`. `dotnet-trace` captures `gc-verbose`
allocation samples and the marker provider. `AnalyzeAllocationTrace.cs` excludes setup, warmup, and cleanup using
the markers and groups allocation-tick weights by object type and allocation stack.

Allocation ticks are samples, not an object census. Their byte weights estimate allocation distribution; they do not
provide exact byte totals or counts for each type. Short-lived allocation is the target of this investigation; a heap
dump would primarily describe objects still present at the snapshot. Diagnostics can alter execution timing and
batch formation, so these runs are not latency benchmarks. See Microsoft's
[dotnet-trace documentation](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/dotnet-trace) for the GC profile.

## Results

Measured 2026-10-22 on Apple M2 Max, macOS 27.0.1, SDK 10.0.100, .NET 8.0.25, OpenTelemetry 1.20.1,
dotnet-trace 10.0.750501, and TraceEvent 3.1.21. Production source matches commit `18f4422`; the diagnostic scripts
were added for this investigation. Each measured window contains 512,000 requests.

| Mode | Process allocated bytes | Bytes per request | Additional bytes per request versus disabled | Allocation samples |
| --- | ---: | ---: | ---: | ---: |
| Disabled | 148,948,768 | 290.92 | — | 1,420 |
| 0% sampling | 699,458,056 | 1,366.13 | 1,075.21 | 6,610 |
| 100% sampling | 864,819,848 | 1,689.10 | 1,398.19 | 8,181 |

All traces reported zero lost events and zero allocation samples without stacks. Summed allocation-tick weights were
151,318,976 / 704,628,352 / 872,066,408 bytes, respectively. They differ from the process counters by approximately
0.7–1.6%; per-type sampling uncertainty can be larger than this aggregate discrepancy.

The following values divide sampled type weights in the 0% run by 512,000 requests. They are approximate allocation
attribution, not exact object sizes, object counts, or isolated incremental costs:

| Observed types | Estimated bytes per request | Resolved allocation site |
| --- | ---: | --- |
| `Activity` | 212 | `Activity.Create` reached from `TraceCall` and `StartBatch` |
| `String` | 144 | SDK `ComputeActivitySamplingResult`, `Activity.Start`, and trace/span ID generation |
| `CompleteCallAsync` state machine | 138 | `AsyncTaskMethodBuilder.GetStateMachineBox` in the tracing completion bridge |
| `Task` and `TaskCompletionSource` | 148 | Includes both core completion and the extra tracing completion bridge |
| `SubmissionTraceContext` | 64 | Per-submission correlation state |
| `ActivityLink[]` and link nodes | 148 | `List<ActivityLink>.AddWithResize`, then Activity's internal `DiagLinkedList` construction |
| `ExecutionContext` and `OneElementAsyncLocalValueMap` | 72 | Ambient Activity/context changes |
| Tag nodes, `TagsLinkedList`, and tag enumerators | 109 | Activity enumerates and copies creation-time tags |
| Deduplication hash-set entry arrays | 50 | HashSet growth inside `StartBatch`; excludes bucket arrays |

The disabled profile is dominated by core pending requests, tasks, queue nodes, and completion continuations.
Tracing adds several independent allocation sources; the whole increase cannot be attributed to links or deduplication.
Caching input metadata avoids recreating the input collection, but does not eliminate Activity's own tag storage or
enumeration. Forward links also still get copied into Activity storage in the 0% run.

The 100% profile adds a substantial `ActivityContext[]` component (about 153 estimated bytes per request), with stacks
through `SubmissionTraceContext.AddBatch`, and more link nodes through `SubmissionTraceContext.Complete` /
`Activity.AddLink`. These are consistent with the recorded path collecting reverse links in addition to forward links.

## Optimization implications

No pooling was introduced. The profile supports investigating avoidable list/hash-set growth and repeated tag copying
before adding pools. Reducing creation-time tags changes what samplers can inspect, so moving tags until after sampling
must not silently change the current contract. Reverse links implement Execute-to-handler navigation and are not
discardable metadata under that contract.

The completion bridge is another measurable source, but a replacement must preserve all original task faults,
cancellation tokens, and stopping the span before returning completion. Activities and Tasks are not resettable objects
owned exclusively by RequestBatcher. Submission contexts are shared across queued requests; a pool would need proof
that no request or completion path can access a returned object. The measured data does not justify adding this lifecycle
complexity before evaluating simpler allocation reductions.

Evidence: [disabled types](allocations/disabled-types.csv), [0% types](allocations/sample0-types.csv),
[100% types](allocations/sample100-types.csv), and [workload validation](allocations/validation.txt).
Marker boundaries, sample counts, and lost-event checks are in the matching `*-summary.txt` files under `allocations`.
Full type-plus-stack CSVs remain locally in `/private/tmp/requestbatcher-allocation-analysis/{Disabled,Sample0,Sample100}`
and can be regenerated from the retained traces with the analyzer.

## Reproduction

Use the SDK selected by `global.json`. The trace tool is installed in a scratch directory and does not change project
dependencies. Build the probe before tracing so the captured process is the workload, rather than a build command.

```bash
dotnet tool install dotnet-trace --version 10.0.750501 --tool-path /private/tmp/requestbatcher-diagnostics-tools
dotnet build scripts/ProfileTracingAllocations.cs --configuration Release \
  --output /private/tmp/requestbatcher-allocation-probe
mkdir -p /private/tmp/requestbatcher-allocation-traces
/private/tmp/requestbatcher-diagnostics-tools/dotnet-trace collect \
  --profile gc-verbose --providers RequestBatcher-AllocationProbe \
  --output /private/tmp/requestbatcher-allocation-traces/Sample0.nettrace --show-child-io -- \
  dotnet /private/tmp/requestbatcher-allocation-probe/ProfileTracingAllocations.dll Sample0 500
dotnet run --file scripts/AnalyzeAllocationTrace.cs -- \
  /private/tmp/requestbatcher-allocation-traces/Sample0.nettrace \
  /private/tmp/requestbatcher-allocation-traces/Sample0
```

Repeat the last two commands with `Disabled` and `Sample100`, changing both file names and the probe mode.
Raw traces remain under `/private/tmp/requestbatcher-allocation-traces`; compact analysis is retained with this report.
