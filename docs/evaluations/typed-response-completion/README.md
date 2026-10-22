# Typed response completion

A typed result box lets single response submissions return their completion task directly,
removing the untyped completion task and response-conversion state machine from the fast path.
Tasks are not pooled; results still wait for handler completion and async scope disposal.

Baseline `282c254` versus `05fb75e`, identical benchmark source, Arm64 macOS,
SDK 10.0.100, .NET 8.0.25, workstation GC, BenchmarkDotNet 0.15.8.
Two launches, eight warmups, 20 measurements, 500 ms iteration target.

## Results

Each wave uses 256 requests, batch size 32, concurrency 8, capacity 512, no key and singleton
handlers. Cancellation uses concurrency 1, a blocker and a drain sentinel. Setup/disposal
are excluded; queueing, scheduling, caller task arrays, validation and exception observation
are included. KB means 1,024 bytes; negative changes are reductions.

| Scenario | Before mean | After mean | Time change | Before allocation | After allocation | Allocation change |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Single response success | 217.52 us | 211.09 us | -2.96% | 131.12 KB | 111.29 KB | -15.12% |
| Single response failure | 8,460.76 us | 7,432.72 us | -12.15% | 1,416.94 KB | 574.16 KB | -59.48% |
| Pre-dispatch response cancellation | 8,096.03 us | 6,358.94 us | -21.46% | 469.95 KB | 253.09 KB | -46.15% |
| Request-only single success control | 190.82 us | 199.73 us | +4.67% | 93.91 KB | 93.85 KB | -0.06% |
| Explicit response batch success control | 72.81 us | 73.44 us | +0.87% | 76.38 KB | 76.38 KB | +0.00% |

**Reduced allocation is the supported primary benefit.** Request-only control times vary,
so the small success-path timing gain is not a robust throughput conclusion. Failure and
cancellation savings depend on the benchmark's exception observation pattern.
The explicit control overlaps the baseline timing interval and keeps allocation unchanged.

These are gated in-process waves with cheap handlers, not network operations or per-item
latency. Batch formation can vary. Results do not establish benefits under capacity pressure,
other response types, keyed routing or scoped/transient async disposal.

## Reproduce

```bash
dotnet run --project tests/RequestBatcher.Benchmarks/RequestBatcher.Benchmarks.csproj \
  --configuration Release -- \
  --filter '*ResponseCompletionBenchmarks*' --artifacts /private/tmp/response-completion-results
```

## Observability

This historical response-completion evaluation does not measure the built-in RequestBatcher
instrumentation. See the [observability design](../../observability-design.md) for the current tracing contract.
