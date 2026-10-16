# Named versus unnamed batching

This benchmark compares the same `IRequestBatcher<int, int>` pipeline registered with and
without `batcherName`. It changes only named registration and the matching service resolution.
It does not compare request wrappers with primitive requests.

## Contract and method

Each measured operation submits 256 integer requests and awaits every response. The response
must equal its request, and every input must reach the handler exactly once. Single submission
and explicit array submission are separate comparison groups, each with an unnamed baseline.
Singleton and scoped handler lifetimes are separate parameter values.

Both variants use the same handler implementation, input array, batch size 32, maximum
concurrency 8, capacity 512, wait mode and one queue partition. Each benchmark case builds one
provider containing only its selected variant. The handler uses a shared wave gate so all
requests are admitted before any handler completes; batch boundaries can still vary with
scheduling. A scoped handler obtains the same singleton wave state through constructor
injection. The handler's response work is deliberately cheap to expose pipeline overhead.

Provider creation, initial batcher resolution and disposal are outside measurement. This
models a batcher injected once into a constructor or action. Scoped handler resolution and
scope disposal still occur per batch inside measurement. Request submission, scheduling,
task aggregation, gate reset and correctness validation are included identically on both
sides. There is no database, simulated I/O delay or external service.

This adds a benchmark only and changes no production contract. Benchmark invocations validate
response values and exact handler processing counts; a failed validation fails the benchmark.
Results describe steady-state in-process waves, not application startup, repeated service
lookup, HTTP latency or database performance. Async scheduling noise and varying batch
boundaries must be considered when interpreting small timing or allocation differences.

## Results

Measured on 2026-10-16 with macOS 27.0.1, Arm64, SDK 10.0.100, .NET 8.0.25,
Concurrent Workstation GC and BenchmarkDotNet 0.15.8. All eight cases completed with
correctness validation enabled. The [full BenchmarkDotNet report](benchmark-report.md)
includes confidence intervals, standard deviations, ratios and GC statistics.

Times and allocations below are per 256-request wave. The time change is calculated as
`named mean / unnamed mean - 1`; a positive value means the named case took longer.

| Submission | Handler lifetime | Unnamed mean | Named mean | Time change | Unnamed allocation | Named allocation |
| --- | --- | ---: | ---: | ---: | ---: | ---: |
| Single requests | Singleton | 211.05 us | 209.41 us | -0.78% | 90.56 KB | 90.44 KB |
| Single requests | Scoped | 212.61 us | 214.21 us | +0.75% | 95.80 KB | 95.43 KB |
| Explicit array | Singleton | 65.17 us | 66.27 us | +1.69% | 55.82 KB | 55.82 KB |
| Explicit array | Scoped | 67.05 us | 67.22 us | +0.25% | 58.63 KB | 58.63 KB |

This run shows no clear steady-state performance penalty from naming a batcher: timing
confidence intervals overlap in all four pairs and the direction of the difference is not
consistent. Managed allocation is identical to the reported precision for explicit arrays;
single-request differences are below 0.4%, with batch boundaries affected by scheduling.
These measurements do not prove zero overhead in other workloads or in repeated DI lookup.

Benchmark processes ran at normal priority because setting high priority was denied by the
environment. BenchmarkDotNet removed timing outliers as listed in its output. No production
optimization was made based on these small differences.

## Reproduce

```bash
dotnet run --project tests/RequestBatcher.Benchmarks/RequestBatcher.Benchmarks.csproj \
  --configuration Release -- \
  --filter '*NamedBatchingBenchmarks*' --artifacts /private/tmp/requestbatcher-named-batching-results
```

The benchmark uses two launches, five warmups and twelve measured iterations per case with a
300 ms iteration target. BenchmarkDotNet reports time and managed allocation per 256-request
wave, with named/unnamed ratios within each submission group and handler lifetime.
