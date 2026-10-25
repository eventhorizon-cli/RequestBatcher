# Request-only delegate forwarding

Returning the invoker's `ValueTask` directly removes an unnecessary asynchronous wrapper when a delegate has no service
parameters and no response validation. The consumer still awaits that operation before completing requests and returning
the execution slot. Response handlers and handlers with injected services retain their existing validation and scope
disposal boundaries.

The measured benefit is lower allocation. These runs do not establish a material end-to-end latency improvement for
20 ms downstream work.

## Setup

Measured on 2026-10-25, macOS Arm64, .NET SDK 10.0.100, .NET runtime 8.0.25, BenchmarkDotNet 0.15.8. Both sides use one
launch, three warmups, eight measured iterations, and a 300 ms iteration target. The baseline is production code at
`53a864d7b10fb7e4ccf80784db5d834de1ed46d7` with the new benchmark sources. The optimized run changes only the no-service,
request-only branch in `DelegateRequestBatchHandler.HandleAsync`. Benchmark workload logic is identical; a private-field
rename to satisfy formatting has no behavioral effect.

- `DelegateForwardingBenchmarks` isolates a handler invocation. Each operation creates the same incomplete
  `TaskCompletionSource`, invokes the handler before releasing it, awaits the returned operation, and checks exactly-once
  completion. Both paths perform the same asynchronous business work. The reported allocation includes the gate and
  benchmark completion machinery, not just forwarding. Delegate binding and warmup are outside measurement.
- `DelegateRequestOnlyBenchmarks` exercises the public pipeline with 256 requests per wave, batch size 32, concurrency 8,
  capacity 512, and one partition. Single and explicit-list submissions use either `Task.Yield()` or a simulated 20 ms
  delay per Handler batch. Every wave validates the transformation and exactly-once handling of every request ID. Provider
  creation, binding, warmup and disposal are outside measurement. No services are injected and no instrumentation is enabled.

The ordinary typed Singleton Handler is an unchanged control. No Docker or external service is used.

## Isolated forwarding

Values are per handler invocation. Raw data: [before](before-forwarding.csv), [after](after-forwarding.csv).

| Handler | Before mean | After mean | Before allocation | After allocation |
| --- | ---: | ---: | ---: | ---: |
| Typed control | 1.801 us | 1.842 us | 316 B | 319 B |
| Delegate | 1.861 us | 1.826 us | 455 B | 319 B |

The delegate operation allocates about 136 B less, matching the typed control after the change. The small variation in
control allocation comes from asynchronous completion scheduling. Timing confidence intervals overlap; this is evidence
for removing an allocation, not for a meaningful latency improvement.

## Public pipeline

Values are per 256-request wave. Raw data, including error and standard deviation: [before](before-pipeline.csv),
[after](after-pipeline.csv). Allocations use BenchmarkDotNet's KB display (1024 bytes).

| Work | Submission | Handler | Before mean | After mean | Before allocation | After allocation |
| --- | --- | --- | ---: | ---: | ---: | ---: |
| Yield | Single | Typed control | 157.07 us | 174.88 us | 86.68 KB | 90.68 KB |
| Yield | Single | Delegate | 160.60 us | 173.96 us | 91.88 KB | 89.22 KB |
| Yield | Explicit list | Typed control | 97.82 us | 93.88 us | 41.36 KB | 41.35 KB |
| Yield | Explicit list | Delegate | 93.11 us | 87.19 us | 42.38 KB | 41.35 KB |
| 20 ms delay | Single | Typed control | 39.359 ms | 37.219 ms | 72.53 KB | 72.84 KB |
| 20 ms delay | Single | Delegate | 37.181 ms | 36.828 ms | 73.25 KB | 72.14 KB |
| 20 ms delay | Explicit list | Typed control | 21.193 ms | 21.088 ms | 42.98 KB | 42.98 KB |
| 20 ms delay | Explicit list | Delegate | 21.267 ms | 21.209 ms | 44.04 KB | 42.98 KB |

Explicit-list allocation is a clearer comparison because the submitted list forms eight full batches. With 20 ms work,
the delegate saves about 1.06 KB per 256-request wave and reaches the typed control's allocation. End-to-end time remains
about 21 ms. Savings are per Handler invocation, not per request.

Single submissions can form partial batches while more requests arrive. This changes the number of Handler calls and
can require another dispatch round, explaining wave durations above 20 ms. The unchanged typed control also varies in
time and allocation, so single-submission differences cannot be attributed entirely to forwarding. Yield-based timing
likewise depends on thread-pool scheduling. One-launch measurements do not establish a general throughput gain.

## Correctness

Both before/after benchmark runs completed all ten cases and validated every measured operation/wave.
Deterministic tests cover incomplete Task-backed and `IValueTaskSource`-backed operations, exactly-once source consumption,
synchronous and asynchronous failures, cancellation, concurrency-slot retention and disposal draining. Existing response
validation and asynchronous scope-disposal tests also pass.

`DelegateRequestOnlyStressTests` exercises single, explicit-list and mixed submissions with eight producers, four waves
of 2,048 requests per case, capacity 256, batch size 32 and concurrency eight. It checks each caller completes only after
its requests are processed, every request's transformation, and exactly-once handling across all waves.

Final validation passed the complete unit-test project on both .NET 8 and .NET 10: 483 tests per framework, including
24,576 request-only stress requests per framework. The solution build and formatting verification also passed.

## Reproduce

Use the same benchmark sources on the baseline and optimized production code, with no concurrent builds or tests during
measurement. Replace the artifact directory with a distinct path for each run:

```bash
dotnet run --project tests/RequestBatcher.Benchmarks/RequestBatcher.Benchmarks.csproj \
  --configuration Release -- \
  --filter '*DelegateRequestOnlyBenchmarks*' '*DelegateForwardingBenchmarks*' \
  --artifacts /private/tmp/requestbatcher-delegate-forwarding-after

dotnet test tests/RequestBatcher.Tests/RequestBatcher.Tests.csproj \
  --configuration Release --filter 'FullyQualifiedName~Delegate'
```

The first suite measures complete submission and processing; the isolated suite deliberately bypasses queueing and
per-submission binding checks. Neither measures first-call expression compilation, injected service costs, response
validation, or tracing overhead. Those remain separate scenarios in the [delegate evaluation](../README.md).
