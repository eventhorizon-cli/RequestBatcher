# Typed versus delegate handlers

`DelegateHandlerBenchmarks` compares the existing typed-handler path with
`AddDelegateRequestBatcher` and the `ExecuteAsync` delegate extensions. Each pair
uses `int` requests and responses, the same business logic, a 256-request wave,
capacity 512, wait mode and one queue partition. Batch size varies across 1, 32
and 256, and concurrency across 1 and 8, producing 48 cases.
Each case builds one provider and one pipeline. Single and explicit submissions
are separate baseline groups, with and without scoped service injection.

The handler logic waits on a shared wave gate and returns each input unchanged.
Each wave validates every response and that each request was processed exactly
once. Both variants access the same per-process wave state; the no-service cases
inject no service parameters into the delegate. The scoped cases use the same
async-disposable dependency and transformation. Typed scoped handler and response
adapter allocations are part of the existing typed pipeline; delegate forwarding
uses its singleton generic handler and a per-batch service scope.

Provider setup, batcher resolution, initial delegate binding/compilation, warmup
and final disposal are outside measurement. Both variants warm the selected
submission shape once during setup. Submission, delegate-binding checks,
scheduling, processing, per-batch service resolution/disposal, task aggregation,
gate reset and validation are included. Caller-side submission delegates are
used identically by the shared benchmark runners on both sides.

First-use reflection and expression compilation add startup latency and are not
measured by these steady-state results. No database or external service is used.
These gated in-memory measurements do not establish HTTP or downstream latency,
or guarantee zero overhead. Batch boundaries may vary with scheduling.

## Coverage

| Suite | Cases | Purpose |
| --- | ---: | --- |
| `DelegateHandlerBenchmarks` | 48 | Single/list response submissions, no services/scoped service, three batch sizes, two concurrency limits |
| `DelegateServiceInjectionBenchmarks` | 12 | Request-only lists, singleton/scoped/transient dependencies, ordinary/keyed service resolution |
| `DelegateReturnTypeBenchmarks` | 12 | Synchronous response work returning `void`, completed `Task` or completed `ValueTask`, versus a typed handler, at three batch sizes |
| `DelegateFirstSubmissionBenchmarks` | 8 | First response submission on a fresh pipeline, default/named registration, with/without a scoped dependency |

All 80 cases validate completed work. Steady-state suites warm the selected call
site before timing. Service injection uses an async-disposable dependency with
the same lifetime on both sides. The typed handler is scoped in every injection
case, while the delegate's singleton forwarding handler owns a scope per batch;
this comparison includes that architectural allocation difference.

Return-type cases perform the same synchronous work and return already-completed
awaitables. They measure return adaptation and scheduling, not asynchronous I/O.
They do not use a gate, so scheduling can affect actual batch sizes. Request-only
and asynchronous-response suites use the same wave gate on both paths.

First-submission cases construct a fresh provider and resolve the batcher in
`IterationSetup`, then dispose it in `IterationCleanup`, outside timing. The
measured submission includes binding and expression compilation, first dispatch,
service resolution if requested, response completion and validation. Each
iteration invokes the benchmark exactly once. The job uses two process launches,
zero warmups and eight iterations; process/JIT first-use effects can contribute
to the first iteration. This is a fresh-pipeline measurement, not total process
startup or a pure expression-compilation microbenchmark.

## Reproduce

```bash
dotnet run --project tests/RequestBatcher.Benchmarks/RequestBatcher.Benchmarks.csproj \
  --configuration Release -- \
  --filter '*DelegateHandlerBenchmarks*' --artifacts /private/tmp/requestbatcher-delegate-results
```

The job uses two launches, five warmups, twelve measured iterations per case and
a 300 ms iteration target. BenchmarkDotNet reports time and managed allocation
per 256-request wave, with the typed handler as the baseline for each pair.

Run all four suites by changing the filter to `'*Delegate*Benchmarks*'`, or select
one suite by its class name. First-submission measurements are per request rather
than per 256-request wave. A full matrix can take several minutes.

For a quick executability check of all cases, override the job counts:

```bash
dotnet run --project tests/RequestBatcher.Benchmarks/RequestBatcher.Benchmarks.csproj \
  --configuration Release -- \
  --filter '*Delegate*Benchmarks*' \
  --iterationCount 1 --warmupCount 0 --launchCount 1 --invocationCount 1 --unrollFactor 1 \
  --artifacts /private/tmp/requestbatcher-delegate-smoke
```

These single-iteration checks do not support performance conclusions. Do not add
`--job Dry` to the attributed jobs: BenchmarkDotNet adds another job rather than
replacing the existing one.

## Results

The [recorded run](results.md) completed the original eight cases on 2026-10-16
using .NET 8.0.25 on macOS Arm64, with batch size 32 and concurrency 8. It does not
represent results for the expanded matrix. Values are per 256-request wave.

| Submission | Services | Typed mean | Delegate mean | Typed allocation | Delegate allocation |
| --- | --- | ---: | ---: | ---: | ---: |
| Explicit list | None | 71.78 μs | 63.36 μs | 55.82 KB | 55.82 KB |
| Explicit list | Scoped | 66.37 μs | 63.43 μs | 59.51 KB | 59.13 KB |
| Individual | None | 209.75 μs | 212.59 μs | 90.26 KB | 90.60 KB |
| Individual | Scoped | 212.09 μs | 210.04 μs | 97.21 KB | 96.46 KB |

Single-submission means differ by about 1%, within this run's variation. Explicit
list means favor the delegate path, but the no-service typed baseline has a wide
spread (8.50 μs standard deviation); this is insufficient evidence for a general
speedup. Scoped delegate batches avoid allocating typed handler/response-adapter
instances, while actual batch boundaries and asynchronous scheduling also affect
allocation. The no-service explicit-list allocations are equal in this run.

This run shows no clear steady-state regression from delegate binding checks or
the cached invoker. It does not measure the first-call compilation cost or prove
that performance is identical for every workload. Benchmark processes used normal
priority because raising priority was unavailable on this machine.
