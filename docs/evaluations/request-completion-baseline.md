# Request Completion Baseline

This benchmark records the allocation and completion cost of the current
request completion primitives. It is an in-process baseline; it does not
measure queue admission, handler execution, or network I/O.

Run it from the repository root with:

```bash
NuGetAudit=false dotnet run \
  --project tests/RequestBatcher.Benchmarks/RequestBatcher.Benchmarks.csproj \
  --configuration Release --no-build -- \
  --filter '*RequestCompletionBenchmarks*'
```

The recorded run used .NET 8.0.25 on an Arm64 macOS host, with one launch,
three warmups, and eight measured iterations. Values below are mean time and
managed allocation per benchmark operation.

| Completion path | 128 requests | 1,024 requests |
| --- | ---: | ---: |
| Single request success | 2.987 us / 20.00 KB | 27.071 us / 160.00 KB |
| Explicit batch shared success | 1.866 us / 9.16 KB | 14.879 us / 72.16 KB |
| Single request failure | 50.338 us / 77.83 KB | 396.675 us / 622.74 KB |
| Explicit batch shared failure | 4.014 us / 9.95 KB | 28.658 us / 72.95 KB |
| Single request cancellation | 9.050 us / 41.13 KB | 80.971 us / 328.13 KB |

Explicit submissions already share one completion source and use roughly half
the allocation of independent single-request completion in these primitive
success workloads. This suite has no waiting continuations, response facade,
queue admission, or concurrent handler work. Its failure cases do not observe
the faulted tasks, so exception-holder finalization can contribute to their
reported cost. Cancellation also includes array allocation and token-source
registration work; this suite does not isolate those costs.

These measurements do not establish whether a typed result box or a different
completion path would help. Production decisions require a before/after public
pipeline comparison with real awaiting callers, observed errors, and validated
responses. Asynchronous tasks remain caller-owned and are not pooled.
