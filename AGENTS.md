# AGENTS.md

You are an AI coding assistant for this repository.

## Scope and working style

- These instructions apply repository-wide unless a more specific `AGENTS.md` exists below the target path.
- Follow [`.editorconfig`](.editorconfig) and nearby code before introducing a new style or abstraction.
- Keep changes focused, maintainable, and production-ready. Preserve unrelated user changes.
- Use the SDK selected by [`global.json`](global.json); do not change the SDK or target-framework policy incidentally.

## Architecture

- Keep the public API small and centered on single-request and explicit batch submission through
  `IRequestBatcher<TRequest>`.
- BufferQueue is an internal scheduling dependency. Do not expose its topics, partitions, producers, or consumers from
  RequestBatcher public APIs.
- Keep queue adapters and pending-request state under `src/RequestBatcher/PendingRequests`, consumer supervision and
  dispatch under `src/RequestBatcher/Scheduling`, and logging under `src/RequestBatcher/Diagnostics`; the coordinator
  owns public submission and lifecycle composition.
- Compose RequestBatcher through `IServiceCollection.AddRequestBatcher`; callers must not need to register or configure
  BufferQueue. Production code registers BufferQueue into the application's container and must not build or own a
  nested service provider.
- Register the handler in the same `AddRequestBatcher` call and require an explicit `ServiceLifetime`. Resolve scoped and
  transient handlers inside an async scope owned by one batch; never capture them in the singleton coordinator.
- A request accepted by the coordinator must complete exactly once with success, cancellation, or the handler failure
  for its batch. Capacity must be released exactly once on every terminal path.
- RequestBatcher provides no global, partition-local, or partition-key handler execution ordering guarantee.
  `UsePartitionKey` routes equal keys to one queue partition only; separate batches for equal keys can execute
  concurrently. Do not imply that an entire key is collected into one batch or that routing serializes execution.
- Caller cancellation may remove work only before handler dispatch. Once a batch is dispatched, report its actual
  outcome so retrying callers cannot unknowingly duplicate side effects.
- `StopAsync` and `DisposeAsync` stop admission before draining accepted work. Keep lifecycle transitions idempotent
  and safe under concurrent calls.
- Keep one top-level type per C# file, except for nested implementation details and test helpers. Namespaces mirror the
  owning project and source directory.

## C# and dependencies

- Target the frameworks declared by project files and use only C# 12 language features.
- Keep nullable annotations, cancellation propagation, and `ConfigureAwait(false)` usage correct and consistent.
- Do not add per-file copyright or license headers. Repository licensing is defined by the root `LICENSE` file and
  package metadata.
- Write code comments and XML documentation in English. Document every public API type and member; do not suppress
  `CS1591` project-wide.
- Prefer concise modern C# when it clarifies the code. Use primary constructors when a type only captures dependencies
  or state and doing so does not broaden constructor accessibility. Retain explicit constructors when validation,
  defensive copies, resource ownership, cleanup, or non-public constructor visibility make the lifecycle clearer.
- Prefer base libraries and existing dependencies. Explain any new production dependency and compatibility impact.
- Do not add a repository `NuGet.config` or hard-code a package source.
- Do not decompile NuGet package assemblies. When implementation details of a dependency are necessary, inspect the
  corresponding source repository at the commit recorded in the package metadata.

## Documentation

- For a feature, public API, behavior, or architectural change, create or update the relevant design document before
  editing production code. The design must state the intended contract, internal flow, compatibility impact, and test
  coverage; implementation must follow that documented design.
- Design documents describe only the current intended design. Do not retain implementation history, rejected
  alternatives, superseded behavior, or APIs that no longer exist.
- Update the README when behavior, configuration, public APIs, lifecycle, or user-facing functionality changes.
- Keep `src/RequestBatcher/README.md`, the NuGet package README, synchronized with relevant user-facing behavior,
  configuration, and usage guidance in the root README.
- Keep usage examples compilable and focused on the public abstraction rather than internal queue mechanics.
- Document the durability boundary explicitly: RequestBatcher is in-process and does not recover pending requests after a
  process failure.

## Testing and validation

- Add or update deterministic unit tests for behavior changes, especially batching, concurrency, cancellation,
  backpressure, exception fan-out, and shutdown.
- Use strict Moq mocks for interaction boundaries. Prefer stateful test collaborators only when a mock would obscure
  concurrency or lifecycle behavior.
- Unit tests must not require network access or external services.
- Keep database performance comparisons in `tests/RequestBatcher.Benchmarks`, not in the unit-test project. Benchmark
  container startup, schema creation, pool warmup, and table reset must stay outside measured regions.
- Keep direct and batched database paths comparable: use the same logical requests, schema, connection pool limit,
  commit semantics, and validation. Verify persisted row counts so a faster result cannot hide dropped work.
- Partition-key tests must prove both sides of the contract: equal keys route consistently while separate batches for
  equal keys can execute concurrently. Duplicate-merging examples must remain correct when duplicates span multiple
  batches.
- Batch-submission tests must cover all-or-nothing admission, capacity waiting, handler failure, and per-item partition
  routing. A submitted batch must not be documented as one handler invocation.
- Name tests `MemberOrBehavior_Scenario_ExpectedOutcome`, with PascalCase within each segment and underscores between
  the three segments.
- Avoid timing-only assertions where a completion source or another deterministic synchronization primitive can
  express the same condition.
- After C# changes, run the narrowest relevant tests while iterating, then the complete test project and formatting.
  In a dirty worktree, pass `--include` with the C# files changed by the task so formatting does not modify unrelated
  work. CI remains responsible for verifying that the committed tree needs no formatting changes.

Standard checks from the repository root:

```bash
dotnet restore RequestBatcher.slnx
dotnet format RequestBatcher.slnx --no-restore
dotnet build RequestBatcher.slnx --configuration Release --no-restore
dotnet test tests/RequestBatcher.Tests/RequestBatcher.Tests.csproj --configuration Release --no-build --no-restore
dotnet pack src/RequestBatcher/RequestBatcher.csproj --configuration Release --no-build --no-restore
docker compose -f samples/RequestBatcher.Deduplication/compose.yaml up -d
dotnet run --project samples/RequestBatcher.Deduplication/RequestBatcher.Deduplication.csproj --no-build --no-restore
```

The PostgreSQL and Redis benchmarks are opt-in and require a running Docker daemon:

```bash
dotnet run --project tests/RequestBatcher.Benchmarks/RequestBatcher.Benchmarks.csproj \
  --configuration Release -- \
  --filter '*PostgreSqlWriteBenchmarks*'

dotnet run --project tests/RequestBatcher.Benchmarks/RequestBatcher.Benchmarks.csproj \
  --configuration Release -- \
  --filter '*Redis*Benchmarks*'
```
