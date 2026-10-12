# Typed response completion

Single response submissions use a typed result box, derived from
`TaskCompletionSource<TResponse>`, as the pending request's completion target.
When enqueueing completes synchronously, the public facade returns that task
directly. This removes the untyped completion task and the response-conversion
async state machine from that path. Capacity waits retain the producer's
observation path. Tasks are not pooled, and completion still uses
`RunContinuationsAsynchronously`.

Success is published only after the handler, validation of every response in its
handler batch, and async scope disposal succeed. Ordinary failures retain their
exception identity. A handler cancellation retains its token and canceled task
status, but a single response now throws the standard `TaskCanceledException`
rather than the handler's original cancellation exception instance.

## Method

`ResponseCompletionBenchmarks` exercises public DI registration, enqueueing,
dispatch, and awaiting callers. One operation submits and validates 256 requests,
with batch size 32, maximum concurrency 8, and queue capacity 512. No partition
key is configured. Handlers are singletons. Setup and provider disposal are
outside the measurements.

For success and failure waves, a handler gate is released only after caller
waiters have been attached. Each failing handler batch throws its own exception;
all 256 returned tasks are awaited and their failures inspected. Cancellation
uses concurrency 1, a separate blocked request to prevent target dispatch, and
a drain sentinel before the next wave. Its measurements include token sources,
registrations, blocker/sentinel requests, and observing all canceled tasks.
Explicit response batches and request-only singles are controls.

Before and after use the same benchmark source, run sequentially on the same
host. The baseline is commit `282c254d291641901faa3659bd1a606ddf393b6f`, with only
the new benchmark file copied into a clean archive. The candidate is commit
`05fb75e3777d3f9ede86facdd451abfa00965dc9`. The benchmark source SHA-256 is
`cea1504c8cdac7fe6700c1ae03dc3ed96a7e86894a523fd5dfa1fd5be8cb46ca`
for both runs. The job uses two processes,
eight warmup iterations, and twenty measurement iterations per process, with
500 ms target iteration time. The environment is .NET SDK 10.0.100, runtime
8.0.25, ARM64 macOS, workstation GC, and BenchmarkDotNet 0.15.8. High process
priority and processor-model discovery were unavailable in the sandbox.

Reproduce each version with:

```bash
NuGetAudit=false dotnet run \
  --project tests/RequestBatcher.Benchmarks/RequestBatcher.Benchmarks.csproj \
  --configuration Release -- \
  --filter '*ResponseCompletionBenchmarks*' --artifacts /tmp/response-completion-results
```

## Measured comparison

Each row is one 256-request wave. Time change is `(after / before - 1)`;
negative values are reductions. KB denotes 1,024 bytes. These are the full-suite
results, with the controls retained rather than omitted.

| Scenario | Before mean | After mean | Time change | Before allocation | After allocation | Allocation change |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Single response success | 217.52 us | 211.09 us | -2.96% | 131.12 KB | 111.29 KB | -15.12% |
| Single response failure | 8,460.76 us | 7,432.72 us | -12.15% | 1,416.94 KB | 574.16 KB | -59.48% |
| Pre-dispatch response cancellation | 8,096.03 us | 6,358.94 us | -21.46% | 469.95 KB | 253.09 KB | -46.15% |
| Request-only single success control | 190.82 us | 199.73 us | +4.67% | 93.91 KB | 93.85 KB | -0.06% |
| Explicit response batch success control | 72.81 us | 73.44 us | +0.87% | 76.38 KB | 76.38 KB | +0.00% |

The supported primary benefit is reduced allocation for single responses.
Success is 2.96% faster in this full-suite comparison, but request-only control
means vary between runs. This does not establish a robust success-path throughput
improvement. Failure and cancellation also avoid the removed wrapper's exception
propagation work; their measured savings depend on this benchmark's observation
pattern. Full confidence intervals and GC counts are in the linked reports.

An initial candidate (`da0b3f3`) routed shared completions through the new
interface. Its explicit-batch mean increased to 77.01 us from 72.81 us. A
control-only repeat in reverse execution order (candidate first, baseline
second) confirmed that concern: 78.20 us versus 73.70 us. The final implementation
retains direct calls for shared batch completion; its 73.44 us mean overlaps
both baseline runs' confidence intervals and keeps allocation at 76.38 KB.

Request-only controls did not show a consistent regression: the initial full
suite measured 190.82 us before and 198.76 us after, while the reverse repeat
measured 204.42 us before and 199.45 us after. The final candidate is 199.73 us.
These differences are why small success-time changes are not treated as a
reliable benefit. The reverse repeat's summaries are retained as
[baseline control evidence](interface-trial-before-controls.md) and
[initial-candidate control evidence](interface-trial-after-controls.md), with
[baseline CSV](interface-trial-before-controls.csv) and
[initial-candidate CSV](interface-trial-after-controls.csv).

Full results and raw iterations: [before](before.md), [after](after.md),
[before CSV](before.csv), [after CSV](after.csv),
[before process output](before-output.txt), [after process output](after-output.txt).

## Interpretation limits

These are in-process waves with cheap handlers, not Redis or database round
trips. The reported means describe whole waves, not individual request latency
or p99. Allocation includes queueing, scheduling, task arrays, and validation;
it is not an isolated object-size measurement. Concurrent scheduling can change
the number and shape of handler batches. Failure and cancellation times include
throwing and observing exceptions. Results do not establish a benefit for
capacity-saturated queues, other response types, remote I/O, partition-key
workloads, or scoped/transient handlers with async disposal. Deterministic tests
cover the disposal boundary; these benchmarks do not measure its cost.

## Correctness validation

All 116 unit tests pass, including 13 added tests for publication after handler
and async scope disposal, disposal/handler failure identity, missing-response
fan-out, cancellation before and after dispatch, capacity waits and rejection,
early lifecycle rejection, and repeated awaits. Formatting, Release solution
build, and package creation pass. No asynchronous task is pooled, and explicit
submissions still wait for all of their items through one shared completion.
