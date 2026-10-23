# Pending tracing completion evaluation

## Contract

Compare completion implementations with every input Task still pending when its wrapper is registered. Synchronous
completion is a compatibility fast path, not the basis for an allocation claim. Keep the public Task-returning API;
changing it to ValueTask would be a breaking change and callers commonly aggregate submissions with Task.WhenAll.

The isolated benchmark compares the existing async Task helper plus a completion source, an ordinary async ValueTask
helper plus the same completion source, and an awaiter callback whose state also owns the completion source. All must
complete the public task only after the tracing completion action, retain the captured ExecutionContext for that action,
and preserve the full fault list, faulted OperationCanceledException, and cancellation tokens. No pool or async void
completion is allowed. ValueTask here evaluates internal plumbing only, without changing caller consumption rules.

The microbenchmark includes identical source tasks and submission counts in each case and validates outcomes and
completion action counts. It is not an I/O benchmark. Adopt a replacement only after regression tests and a separate
20 ms end-to-end benchmark demonstrate useful allocation reduction without semantic changes. The matched end-to-end
comparison uses source commit `c66f0c8` as the pre-change implementation, preserving the earlier package measurements.

## Measurement

The run used .NET 8.0.25 on arm64 with BenchmarkDotNet 0.15.8, two launches, four warmup iterations, twelve
measured iterations, eight invocations per iteration, and `MemoryDiagnoser`. Each invocation registered 1,024
wrappers while every source task was still pending, then completed all sources and awaited all public tasks. The
completion action checked the captured `AsyncLocal` value and the exact callback count.
The measured workload covers successful completion only. Fault and cancellation transfer branches are present in
the prototypes, but this benchmark does not validate those branches and is not a replacement for regression tests.

| Strategy | Mean per 1,024 requests | Allocated per invocation | Relative allocation |
| --- | ---: | ---: | ---: |
| Async Task helper + TCS | 593.9 us | 489.72 KB | 1.00 |
| Async ValueTask helper + TCS | 619.3 us | 490.14 KB | 1.00 |
| Awaiter callback object + TCS | 539.1 us | 496.68 KB | 1.01 |

The callback object was faster in this isolated pending-only run, but allocated about 1.4% more than the current
Task helper. The ordinary ValueTask helper did not reduce allocation and was about 4.3% slower. The callback and
Task-helper 99.9% confidence intervals overlap, so this result is evidence for further investigation rather than a
production replacement recommendation. It also excludes Activity creation, link collection, queue scheduling, and
downstream I/O.

## ValueTask and BufferQueue applicability

The [ValueTask await article](https://www.cnblogs.com/eventhorizon/p/21973620) is relevant to consumption rules and
synchronous fast paths. Here, every source remains pending at registration, so a frequent synchronous-completion
assumption cannot justify replacing the completion bridge.

BufferQueue 2.0.3 records source commit `90c6208f940592f797ab8cf78ad4af88d1163649` in its package metadata. Its
[PendingDataValueTaskSource](https://github.com/eventhorizon-cli/BufferQueue/blob/90c6208f940592f797ab8cf78ad4af88d1163649/src/BufferQueue/PendingDataValueTaskSource.cs)
wraps `ManualResetValueTaskSourceCore<T>` with asynchronous continuations and a versioned ValueTask. The
[consumer](https://github.com/eventhorizon-cli/BufferQueue/blob/90c6208f940592f797ab8cf78ad4af88d1163649/src/BufferQueue/BufferPullConsumer.cs)
owns one reusable source for its serial wait-for-data loop, coordinates reset and notification under a lock, and
converts a pending wait with `AsTask().WaitAsync(cancellationToken)` to support cancellation.

That source's reuse is tied to one consumer's sequential wait lifecycle. Request submissions can overlap, and their
public Tasks support aggregation and multiple awaits; reusing their completion sources would require a separate
ownership and consumption protocol. The ordinary internal ValueTask prototype does not provide that reuse, as the
allocation measurements show. Keep the current Task bridge and avoid introducing a completion pool without measured
benefit and lifecycle coverage.

Raw output is retained in [completion-benchmark.csv](completion-benchmark.csv) and
[completion-benchmark.txt](completion-benchmark.txt). The source is
[TracingCompletionBenchmarks.cs](../../../tests/RequestBatcher.Benchmarks/InMemory/TracingCompletionBenchmarks.cs).

Run it from the repository root with:

```bash
dotnet run --project tests/RequestBatcher.Benchmarks/RequestBatcher.Benchmarks.csproj \
  --configuration Release -- \
  --filter '*TracingCompletionBenchmarks*'
```
