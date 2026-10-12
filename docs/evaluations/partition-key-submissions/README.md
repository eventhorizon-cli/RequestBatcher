# Partition-key submission comparison

## Workload contract

Compare one explicit `ProcessAsync(requests)` submission with issuing
`ProcessAsync(request)` for every item and then awaiting `Task.WhenAll`. Each
operation uses the same ordered integer IDs, with 10, 100, or 1,000 requests.
Single submission means concurrent outstanding calls, not awaiting each item
before issuing the next. Both methods await all work and validate every ID was
processed exactly once.

Both methods use `UsePartitionKey(request => request % 64, partitionCount: 4)`.
Equal keys route consistently; an explicit group may span several partitions
and several handler invocations. No execution ordering is assumed. The handler
batch limit stays at 100, concurrency at 8, and pending capacity at 2,048. Input
size is independent of the handler batch limit.

The handler asynchronously delays once per actual invocation with `Task.Delay`
for either 1 ms or 10 ms, then records each processed ID. This models a fixed
per-call downstream wait. It does not model per-item CPU cost, a real network,
or deduplication. OS timer resolution and scheduling can extend the delay.
There is no gate forcing submissions to accumulate before dispatch: natural
batch formation is part of this comparison.

## Measurement

DI registration, resolution, input creation, and async disposal are outside the
measured methods. Each measured operation includes resetting validation state,
submission, waiting, and validating all item counts. The single-call task array
and `Task.WhenAll` are included; the explicit path awaits its shared completion.
`MemoryDiagnoser` reports allocation per complete operation. The single path is
the baseline for each matched input-size/delay pair.

Use two launches, five warmups, twelve measured iterations, and a 300 ms
iteration target on .NET 8. These are whole-wave timings, not per-item or p99
latencies. Benchmark source changes only; public API and production behavior
are unchanged. Build, format, and the full unit suite verify integration; each
benchmark operation independently validates no missing or duplicate work.

```bash
dotnet run --project tests/RequestBatcher.Benchmarks/RequestBatcher.Benchmarks.csproj \
  --configuration Release -- \
  --filter '*PartitionKeySubmissionBenchmarks*'
```
