# Per-batch dependency-injection scope cost

This evaluation measures the scope boundary used by a delegate Handler whose only
service parameter is a singleton. It measures the existing implementation; it does
not introduce lifetime inference or change production behavior.

## Workload and reproduction

Source: [BatchScopeBenchmarks.cs](../../../../tests/RequestBatcher.Benchmarks/InMemory/BatchScopeBenchmarks.cs).

One measured operation represents one Handler batch. The service cases process one
reused request with the same synchronous transformation and validation. The four
cases are:

- `RootSingletonResolution`: resolve the singleton from the root provider and call
  the static Handler directly. This is a measurement control, not a new delegate API.
- `EmptyBatchScope`: create an otherwise unused scope through a cached
  `IServiceScopeFactory` and dispose it using `await using`.
- `BatchScopeSingletonResolution`: create a scope, resolve the singleton, call the
  same static Handler, and dispose the scope.
- `DelegateBatchScopeSingletonResolution`: call the actual bound
  `DelegateRequestBatchHandler`, which resolves the singleton inside its batch scope.

Provider construction, first singleton resolution, delegate binding/expression
compilation, and initial warm-up happen outside measurement. Service cases check
the resolved instance, the transformation result, and exactly-once processing on
every operation. Cleanup verifies that the singleton was not disposed by a batch
scope and is disposed exactly once by the root provider. Empty-scope operations
check that the child provider is distinct from the root.

The provider uses `ValidateScopes = true` and `ValidateOnBuild = true`.
BenchmarkDotNet performs one launch, five warm-up iterations and twelve measured
iterations, with a target iteration duration of 300 ms. Measurements run without
concurrent builds, tests, or other benchmarks.

```bash
dotnet build tests/RequestBatcher.Benchmarks/RequestBatcher.Benchmarks.csproj \
  --configuration Release --no-restore

dotnet run --project tests/RequestBatcher.Benchmarks/RequestBatcher.Benchmarks.csproj \
  --configuration Release --no-build --no-restore -- \
  --filter '*BatchScopeBenchmarks*' \
  --artifacts /private/tmp/requestbatcher-batch-scope-final
```

## Measured results

The current benchmark project targets .NET 10. The recorded measurements below used
.NET 8; remeasure both controls on the same runtime before comparing new results.

Measured on 2026-10-25 using the production behavior at `9be066a`, with this
benchmark and comment-only production edits. Environment: Apple M2 Max, macOS
27.0.1, .NET SDK 10.0.100, .NET runtime 8.0.25 Arm64, BenchmarkDotNet 0.15.8, and
Microsoft.Extensions.DependencyInjection 10.0.11. All four benchmark processes
completed successfully, including their per-operation and disposal assertions.
The exported [CSV](results.csv) retains the job configuration and results.

| Operation | Mean per batch | Error (99.9% CI half-width) | Allocated per batch |
| --- | ---: | ---: | ---: |
| Root singleton resolution and processing | 16.93 ns | 0.106 ns | 0 B |
| Empty scope creation and disposal | 28.03 ns | 0.224 ns | 128 B |
| Scope, singleton resolution and processing | 41.80 ns | 0.152 ns | 128 B |
| Actual delegate, singleton injection and batch scope | 47.41 ns | 0.175 ns | 128 B |

The matched root/scoped pair differs by **24.87 ns and 128 B per batch**. The
actual delegate path adds about 5.61 ns over the manual scoped control in this
synchronous workload, with no additional measured allocation. These small timing
differences describe this local run, not a guarantee for other CPUs or containers.

The 128 B scope allocation is paid once per actual Handler batch. For a full batch
of 32 requests it amortizes to 4 B/request; for a batch of one it remains 128 B/request.
This is an arithmetic allocation breakdown, not a separate batching benchmark.
A 20 ms downstream operation is many orders of magnitude longer than this isolated
scope boundary, but no end-to-end latency gain or GC impact was measured here.
These results do not justify adding complex lifetime inference solely to eliminate
singleton-only scopes. Keep the current scope policy and let the container own
service lifetimes.

Validation also passed the solution Release build with zero warnings/errors,
formatting verification for the two changed C# files, and all 483 unit tests on
each of .NET 8 and .NET 10. Production changes are comments only.

## Interpretation boundaries

The root/scoped service pair isolates the additional scope boundary around the
same work. The empty-scope case excludes service resolution and Handler processing;
its ratio to the root service case is not a performance improvement comparison.
The actual delegate case also includes its cached invoker and forwarding path.
Correctness checks remain in measured service operations, so their absolute means
are not pure DI lookup costs.

All Handler work completes synchronously. Scope disposal also completes
synchronously because the scope owns no disposable service instances: the
singleton belongs to the root provider. These results do not measure suspended
async Handler state, scoped/transient construction and disposal, asynchronous
resource cleanup, queueing, scheduling, concurrent contention, tracing, or an
end-to-end workload with a 20 ms downstream call. Applications with those costs
must measure their own workload.

## Scope ownership

The public `IServiceProvider` abstraction does not expose service lifetimes. The
delegate binder knows parameter types and service keys, so it creates one scope
per batch whenever services are requested. Singleton services are still reused;
they are not constructed or disposed for each batch. Typed singleton Handlers
already bypass batch scopes because their lifetime is explicitly registered.

ASP.NET Core method injection uses the same separation between cached invocation
metadata and per-call service resolution. Conventional Middleware resolves method
parameters from `HttpContext.RequestServices`; MVC Action service binders use the
same provider. The default request-services feature lazily creates one HTTP request
scope. It does not inspect method parameters to skip that scope for singleton-only
injection. RequestBatcher has a batch boundary spanning potentially multiple
callers and therefore owns its own scope instead of borrowing an HTTP request scope.

References: [Middleware binding](https://github.com/dotnet/aspnetcore/blob/main/src/Http/Http.Abstractions/src/Extensions/UseMiddlewareExtensions.cs),
[Action service binding](https://github.com/dotnet/aspnetcore/blob/main/src/Mvc/Mvc.Core/src/ModelBinding/Binders/ServicesModelBinder.cs),
[request scope creation](https://github.com/dotnet/aspnetcore/blob/main/src/Http/Http/src/Features/RequestServicesFeature.cs).
See the [delegate design](../../../delegate-handlers-design.md) for the current contract.
