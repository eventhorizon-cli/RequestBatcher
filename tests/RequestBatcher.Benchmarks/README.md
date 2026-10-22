# Benchmark index

Use this index to find benchmark source and execution requirements. Recorded measurements and conclusions are in the
[evaluation index](../../docs/evaluations/README.md); reports may cover only part of the current parameter matrix.

## Run

Run from the repository root using the SDK selected by `global.json`. Benchmarks target .NET 8.
List available methods, then select a class or method with `--filter`:

```bash
dotnet run --project tests/RequestBatcher.Benchmarks/RequestBatcher.Benchmarks.csproj \
  --configuration Release -- --list flat

dotnet run --project tests/RequestBatcher.Benchmarks/RequestBatcher.Benchmarks.csproj \
  --configuration Release -- --filter '*DeduplicationConcurrencyBenchmarks*'
```

Check the selected class's parameters and job settings before running. Use `--artifacts <directory>` to choose an output directory.

## Batching and handler APIs

These benchmarks run in process without Docker. Some use simulated asynchronous delays.

| Source / filter class | Comparison |
| --- | --- |
| [DeduplicationConcurrencyBenchmarks](InMemory/DeduplicationConcurrencyBenchmarks.cs) | Batch size, concurrency, duplicates and submission size; bulk reads, chunked WhenAll and per-key fan-out with simulated I/O. |
| [TaskWhenAllOverheadBenchmarks](InMemory/TaskWhenAllOverheadBenchmarks.cs) | WhenAll versus awaiting already-started tasks, with completed and pending task sets. |
| [DelayDownstreamOverheadBenchmarks](InMemory/DelayDownstreamOverheadBenchmarks.cs) | Direct and batched calls with simulated 50 ms I/O. Paced arrivals by default; set `REQUEST_BATCHER_TRAFFIC=burst` for bursts. |
| [PartitionKeySubmissionBenchmarks](InMemory/PartitionKeySubmissionBenchmarks.cs) | Concurrent single submissions versus one explicit submission under partition-key routing. |
| [ExplicitSubmissionBenchmarks](InMemory/ExplicitSubmissionBenchmarks.cs) | Explicit groups versus mixed single/group submissions. |
| [NamedBatchingBenchmarks](InMemory/NamedBatchingBenchmarks.cs) | Named versus unnamed pipelines, single/list submissions and singleton/scoped handlers. |
| [DelegateHandlerBenchmarks](InMemory/DelegateHandlerBenchmarks.cs) | Typed versus cached delegate handlers, with and without scoped service injection. |
| [DelegateServiceInjectionBenchmarks](InMemory/DelegateServiceInjectionBenchmarks.cs) | Typed/delegate service injection across dependency lifetimes and ordinary/keyed services. |
| [DelegateReturnTypeBenchmarks](InMemory/DelegateReturnTypeBenchmarks.cs) | Typed handlers versus delegates returning void, completed Task or completed ValueTask. |
| [DelegateFirstSubmissionBenchmarks](InMemory/DelegateFirstSubmissionBenchmarks.cs) | First submission through a fresh typed/delegate pipeline; includes initial delegate binding, excludes provider creation. |

## Completion and allocation

| Source / filter class | Measures |
| --- | --- |
| [BatchCompletionBenchmarks](InMemory/BatchCompletionBenchmarks.cs) | Per-request TCS plus WhenAll versus shared batch completion. |
| [RequestCompletionBenchmarks](InMemory/RequestCompletionBenchmarks.cs) | Internal pending-request success, failure and cancellation, without the full queue pipeline. |
| [PendingRequestAllocationBenchmarks](InMemory/PendingRequestAllocationBenchmarks.cs) | One completed pending request, with and without a cancellable caller token. |
| [ResponseCompletionBenchmarks](InMemory/ResponseCompletionBenchmarks.cs) | Public response success, failure and pre-dispatch cancellation, with request-only and explicit-submission controls. |
| [ExplicitCompletionBenchmarks](InMemory/ExplicitCompletionBenchmarks.cs) | Public explicit-submission success, failure, partial cancellation, capacity waiting and mixed submissions. |
| [BatchErrorAggregationBenchmarks](InMemory/BatchErrorAggregationBenchmarks.cs) | Shared completion accounting for success, repeated exception references and distinct exceptions, sequentially and concurrently. |

## PostgreSQL and Redis

These benchmarks start temporary databases through Testcontainers and require a running Docker daemon.
Container and data preparation are outside measurement. Their results are separate from the historical scheduling-probe experiments.

| Source / filter class | Comparison |
| --- | --- |
| [PostgreSqlWriteBenchmarks](PostgreSql/PostgreSqlWriteBenchmarks.cs) | Direct INSERTs, single submissions and explicit batch submission. |
| [PostgreSqlPriceQueryBenchmarks](PostgreSql/PostgreSqlPriceQueryBenchmarks.cs) | Direct product queries versus batch-local deduplication and bulk reads. |
| [PostgreSqlPriceQueryRateBenchmarks](PostgreSql/PostgreSqlPriceQueryRateBenchmarks.cs) | Paced direct and batched queries across target arrival rates and concurrency limits. |
| [RedisReadBenchmarks](Redis/RedisReadBenchmarks.cs) | Direct GETs, single submissions and explicit batch submission. |
| [RedisWriteBenchmarks](Redis/RedisWriteBenchmarks.cs) | Direct SETs, single submissions and explicit batch submission. |

## Historical scheduling probe

[SchedulingProbe](SchedulingEvaluation/SchedulingProbe.cs) uses `--scheduling-probe`, separately from BenchmarkDotNet.
It cannot run against the current production source: it requires evaluation hooks such as `EvaluationOffload` that are absent.
Reproduction requires the instrumented source described by the [arrival-scheduling evaluation](../../docs/evaluations/arrival-scheduling/README.md).

## Observability

The `RequestBatcher` package exposes `AddRequestBatcherInstrumentation()` for application
tracing. Historical benchmark results in this index are not tracing measurements and should not be read as results with
the new instrumentation enabled. See the [observability design](../../docs/observability-design.md) for span lifetime,
links, sampling, and performance boundaries.

| Source / filter class | Comparison |
| --- | --- |
| [ObservabilityOverheadBenchmarks](InMemory/ObservabilityOverheadBenchmarks.cs) | Disabled, metrics-only, and tracing-plus-metrics OpenTelemetry providers around 256 named async pricing requests with a 20 ms handler delay. |

The corresponding [observability overhead evaluation](../../docs/evaluations/observability-overhead/README.md) records the
measured results and workload limitations.
