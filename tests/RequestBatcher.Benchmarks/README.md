# Benchmark index

Use this index to find benchmark source and execution requirements. Recorded measurements and conclusions are in the
[evaluation index](../../docs/evaluations/README.md); reports may cover only part of the current parameter matrix.

## Run

Run from the repository root using the SDK selected by `global.json`. Benchmarks target .NET 10 and require its runtime.
List available methods, then select a class or method with `--filter`:

```bash
dotnet run --project tests/RequestBatcher.Benchmarks/RequestBatcher.Benchmarks.csproj \
  --configuration Release -- --list flat

dotnet run --project tests/RequestBatcher.Benchmarks/RequestBatcher.Benchmarks.csproj \
  --configuration Release -- --filter '*DeduplicationConcurrencyBenchmarks*'
```

Check the selected class's parameters and job settings before running. Use `--artifacts <directory>` to choose an output directory.

## Pre-commit benchmarks and correctness stress tests

Follow the [critical-path validation rules](../../AGENTS.md#testing-and-validation) before committing changes to
submission, scheduling, completion, lifecycle, or observability hot paths. Select the relevant workloads below.
BenchmarkDotNet repeatedly measures bounded waves; the larger response stress test checks correctness under concurrent
load. Run both when changing shared submission, scheduling, or response-completion logic.

| Changed path | Workloads | Checks / measurements |
| --- | --- | --- |
| Request/response correlation and completion | [ResponseStressTests](../RequestBatcher.Tests/ResponseStressTests.cs), [ResponseCompletionBenchmarks](InMemory/ResponseCompletionBenchmarks.cs) | Stress: single, explicit-batch, and mixed submissions, each with and without partition keys; every response value, batch response order, and exactly-once handling. Benchmarks: 256-request waves, success/failure/cancellation and explicit-response success, time and allocation. |
| Explicit admission, shared completion, backpressure | [ExplicitSubmissionBenchmarks](InMemory/ExplicitSubmissionBenchmarks.cs), [ExplicitCompletionBenchmarks](InMemory/ExplicitCompletionBenchmarks.cs) | Mixed/group submissions; capacity waiting, partial cancellation, failure fan-out, and per-ID processing validation. |
| Queue pulling and dispatch capacity | [QueuePullBenchmarks](InMemory/QueuePullBenchmarks.cs), [DispatchCapacityBenchmarks](InMemory/DispatchCapacityBenchmarks.cs) | 4,096 requests across partition/concurrency settings; synchronous and 20 ms handlers in the dispatch benchmark. |
| Tracing or metrics | [TracingIoOverheadBenchmarks](InMemory/TracingIoOverheadBenchmarks.cs), [MetricsIoOverheadBenchmarks](InMemory/MetricsIoOverheadBenchmarks.cs) | 1,024 requests per wave, single or groups of 32, 20 ms handlers, per-ID exactly-once validation; disabled/sampling or metric-reader configurations. These use request-only handlers. |
| SDK sampling and trace links | [TracingStressTests](../it/RequestBatcher.Observability.IntegrationTests/TracingStressTests.cs) | 32 waves of 512 requests at 0%/100% sampling, mixed submissions, exactly-once handling and actual-batch links; also mixed-sampling policy tests. No Docker needed for this class. |
| Request-only delegate forwarding | [DelegateRequestOnlyStressTests](../RequestBatcher.Tests/DelegateRequestOnlyStressTests.cs), [DelegateRequestOnlyBenchmarks](InMemory/DelegateRequestOnlyBenchmarks.cs), [DelegateForwardingBenchmarks](InMemory/DelegateForwardingBenchmarks.cs) | Eight producers, single/list/mixed submissions, four 2,048-request waves per case; validates every request's transformation and exactly-once handling. Benchmarks measure full-pipeline and isolated forwarding allocation. |
| Database paths | PostgreSQL/Redis benchmarks below | Matched direct/batched operations, result or persisted-data validation; require Docker. |

Run the response correctness stress test:

```bash
dotnet test tests/RequestBatcher.Tests/RequestBatcher.Tests.csproj \
  --configuration Release --filter 'FullyQualifiedName~ResponseStressTests' \
  --logger 'console;verbosity=normal'
```

It uses eight concurrent producers, batch size 32, concurrency eight, pending capacity 256, and explicit groups alternating
between 17 and 129 requests, with smaller final groups. Producers submit their round's operations before awaiting their
results, allowing queue capacity to be exceeded by in-flight work. Each of six cases sends three rounds of 10,000
distinct requests through the same pipeline: 180,000
validated responses per target framework. The asynchronous handler assigns responses in reverse entry order; assertions
do not assume handler execution order. The test runs in the ordinary unit-test suite on .NET 8 and .NET 10.
Its elapsed time is test execution time, not a throughput or latency benchmark result.

Run a relevant performance benchmark, for example:

```bash
dotnet run --project tests/RequestBatcher.Benchmarks/RequestBatcher.Benchmarks.csproj \
  --configuration Release -- --filter '*ResponseCompletionBenchmarks*' \
  --artifacts /private/tmp/requestbatcher-response-benchmarks
```

Run the SDK tracing stress class without starting containers:

```bash
dotnet test tests/it/RequestBatcher.Observability.IntegrationTests/RequestBatcher.Observability.IntegrationTests.csproj \
  --configuration Release --filter 'FullyQualifiedName~TracingStressTests'
```

Record the workload, runtime, commands, correctness outcome, and relevant time/allocation measurements in the
[evaluation reports](../../docs/evaluations/README.md). For optimization claims, compare the same workload against the
baseline with the same environment. Benchmark startup/smoke success alone is insufficient evidence of an improvement.

## Batching and handler APIs

These benchmarks run in process without Docker. Some use simulated asynchronous delays.

| Source / filter class | Comparison |
| --- | --- |
| [DeduplicationConcurrencyBenchmarks](InMemory/DeduplicationConcurrencyBenchmarks.cs) | Batch size, concurrency, duplicates and submission size; bulk reads, chunked WhenAll and per-key fan-out with simulated I/O. |
| [TaskWhenAllOverheadBenchmarks](InMemory/TaskWhenAllOverheadBenchmarks.cs) | WhenAll versus awaiting already-started tasks, with completed and pending task sets. |
| [DelayDownstreamOverheadBenchmarks](InMemory/DelayDownstreamOverheadBenchmarks.cs) | Direct and batched calls with simulated 50 ms I/O. Paced arrivals by default; set `REQUEST_BATCHER_TRAFFIC=burst` for bursts. |
| [QueuePullBenchmarks](InMemory/QueuePullBenchmarks.cs) | One consumer drains 4,096 prefilled BufferQueue records; batch limits 32/128 and 1/16 partitions. See the [pull capacity evaluation](../../docs/evaluations/queue-pull-capacity/README.md). |
| [DispatchCapacityBenchmarks](InMemory/DispatchCapacityBenchmarks.cs) | Direct full batches versus public explicit submission, synchronous/20 ms handlers, concurrency 8/32 and 1/16 partitions. |
| [PartitionKeySubmissionBenchmarks](InMemory/PartitionKeySubmissionBenchmarks.cs) | Concurrent single submissions versus one explicit submission under partition-key routing. |
| [PartitionSubmissionBenchmarks](InMemory/PartitionSubmissionBenchmarks.cs) | Two independent experiments: filter `*SingleSubmissions*` or `*ExplicitSubmissions*`. Each varies Partition 1/4 at BatchSize 64 and MaxConcurrency 4; validates every response, group order, exactly-once handling and concurrency. Records batch formation, query work, time and allocation. See the [evaluation](../../docs/evaluations/partition-submissions/README.md). |
| [ExplicitSubmissionBenchmarks](InMemory/ExplicitSubmissionBenchmarks.cs) | Explicit groups versus mixed single/group submissions. |
| [NamedBatchingBenchmarks](InMemory/NamedBatchingBenchmarks.cs) | Named versus unnamed pipelines, single/list submissions and singleton/scoped handlers. |
| [DelegateHandlerBenchmarks](InMemory/DelegateHandlerBenchmarks.cs) | Typed versus cached delegate handlers, with and without scoped service injection. |
| [DelegateRequestOnlyBenchmarks](InMemory/DelegateRequestOnlyBenchmarks.cs) | Typed singleton versus no-service delegate; 256-request single/list waves with Yield or 20 ms work and per-ID validation. |
| [DelegateForwardingBenchmarks](InMemory/DelegateForwardingBenchmarks.cs) | Isolated typed/delegate forwarding of an incomplete operation, with exactly-once completion validation. |
| [DelegateServiceInjectionBenchmarks](InMemory/DelegateServiceInjectionBenchmarks.cs) | Typed/delegate service injection across dependency lifetimes and ordinary/keyed services. |
| [BatchScopeBenchmarks](InMemory/BatchScopeBenchmarks.cs) | Per-batch scope creation/disposal, root versus scoped singleton resolution, and actual delegate invocation with a singleton. Synchronous isolated work with identity, result, exactly-once processing and disposal checks; no Docker. See the [scope cost evaluation](../../docs/evaluations/delegate-handlers/batch-scope/README.md). |
| [DelegateReturnTypeBenchmarks](InMemory/DelegateReturnTypeBenchmarks.cs) | Typed handlers versus delegates returning void, completed Task or completed ValueTask. |
| [DelegateFirstSubmissionBenchmarks](InMemory/DelegateFirstSubmissionBenchmarks.cs) | First submission through a fresh typed/delegate pipeline; includes initial delegate binding, excludes provider creation. |

## Completion and allocation

| Source / filter class | Measures |
| --- | --- |
| [BatchCompletionBenchmarks](InMemory/BatchCompletionBenchmarks.cs) | Per-request TCS plus WhenAll versus shared batch completion. |
| [RequestCompletionBenchmarks](InMemory/RequestCompletionBenchmarks.cs) | Internal pending-request success, failure and cancellation, without the full queue pipeline. |
| [PendingRequestAllocationBenchmarks](InMemory/PendingRequestAllocationBenchmarks.cs) | One completed pending request, with and without a cancellable caller token. |
| [TracingCompletionBenchmarks](InMemory/TracingCompletionBenchmarks.cs) | Task, ValueTask, and callback tracing completion bridges, comparing 1,024 pending sources per measured invocation. |
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

The optional `RequestBatcher.OpenTelemetry` package exposes `AddRequestBatcherInstrumentation()` for application
tracing. Historical benchmark results in this index are not tracing measurements and should not be read as results with
the new instrumentation enabled. See the [observability design](../../docs/observability-design.md) for span lifetime,
links, sampling, and performance boundaries.

| Source / filter class | Comparison |
| --- | --- |
| [ObservabilityOverheadBenchmarks](InMemory/ObservabilityOverheadBenchmarks.cs) | Disabled, metrics-only, and tracing-plus-metrics OpenTelemetry providers around 256 named async pricing requests with a 20 ms handler delay. |
| [TracingIoOverheadBenchmarks](InMemory/TracingIoOverheadBenchmarks.cs) | Published versions and source snapshots, with tracing disabled or SDK sampling at 0%, 1%, 10%, and 100%, for single and explicit submissions with 20 ms simulated downstream calls. See the [current measurement and reproduction guide](../../docs/evaluations/tracing-io-overhead/README.md) and the [single-partition .NET 10 allocation baseline](../../docs/evaluations/tracing-io-overhead/single-partition-net10.md). |
| [MetricsIoOverheadBenchmarks](InMemory/MetricsIoOverheadBenchmarks.cs) | Metrics disabled, SDK aggregation only, and periodic collection every 1,000 ms or 100 ms, with tracing disabled and 20 ms simulated downstream calls. See the [metrics evaluation](../../docs/evaluations/metrics-io-overhead/README.md). |

The corresponding [observability overhead evaluation](../../docs/evaluations/observability-overhead/README.md) records the
measured results and workload limitations.
