# Shared explicit-batch error accounting

The candidate removes the eagerly allocated synchronization object and records
the first exception reference atomically. Repeated reports of that same failure
avoid a lock and set lookup. A different failure creates a reference-equality
set; every item still performs its own terminal transition and count decrement.
Distinct exception objects, cancellation precedence, capacity release, and
waiting for every accepted item remain unchanged.

The isolated success path saves **24 B/group**. One repeated exception saves
**368 B/group**, including the avoided set and its storage. At 1,000 reports,
sequential repeated-error accounting falls from 18.66 to 6.51 microseconds.
Public handler-failure submissions save about 368 B/group, while their measured
time changes by less than 1%. **No public-pipeline throughput gain is claimed.**

The distinct-error path does not establish a timing benefit. Concurrent first
writers can allocate competing sets before one is published; the 1,000-distinct
parallel case allocated about 430 B more in this campaign. Concurrent timings
also include `Parallel.For` and have substantial variation. This optimization
targets repeated references from a failed handler batch, rather than arbitrary
collections of independently created failures.

## Measurements

The [complete tables](results.md) retain every primitive and public-pipeline
case, including uncertainty, allocations, and the reverse-order checks for noisy
cancellation and mixed-submission cases. Different modes can form different
handler batches; pipeline allocation is consequently not an exact multiple of
the isolated saving. Some runs are multimodal or have short measured iterations.
The initial 1,000-item cancellation slowdown (575 to 670 microseconds) did not
reproduce in reverse order (574 to 578 microseconds); mixed submissions changed
from 867/920 to 861/859 microseconds before/after. These results do not establish
a repeatable regression or a throughput improvement.

The primitive observes `Task.Exception` without rethrowing reused exception
objects: repeatedly awaiting the same exception would grow its stack trace and
measure an unrelated artifact. Exception preparation is outside measurement.
Cleanup validates the complete set using reference identity. Public cases use
the existing gated handlers and validate exactly-once processing, the original
failure, cancellation, and capacity waiting on each operation.
The complete unit-test project passes 323 cases on each of .NET 8 and .NET 10,
including concurrent repeated failures, a held final item, and exceptions with
equal user-defined values but distinct object identities.

## Method and reproduction

Measured 2026-10-18 on Apple M2 Max / Arm64 macOS, SDK 10.0.100, .NET 8.0.25,
BenchmarkDotNet 0.15.8, and BufferQueue **2.0.3** in both variants. The baseline
production source is `fa4af7a5c5c87abfc76f2cf2f11b2d2945d43e9c`.
Two isolated 80-file snapshots have identical benchmark and project source;
only `PendingRequests/BatchSubmissionCompletion.cs` differs.

| SHA-256 | Before | After |
| --- | --- | --- |
| Completion source | `fa1948fdf5c05bf741fc4451a3344a8ee91301e40b03217d2816bf7c40388f93` | `6a949857a63b2e7c01df0cae53184f0451714e54900eb3ef21679f141b9cc956` |
| Snapshot manifest | `e23748f326c9ac642a708e37d6447382a6093389f3d5127c3abe20be4bf00871` | `3bfcd8b26c37f8734ab6503820c38297ae3382be21950626cedb15219b7c8da5` |

Manifest entries are sorted relative paths and SHA-256 hashes, joined as
`path hash` lines without a final newline; `bin`, `obj`, and `.git` are excluded.
Snapshot contents are the library, analyzer, benchmark project, `global.json`,
`Directory.Build.props`, and `.editorconfig`.

Primitive cases use two launches, three warmups, eight measured iterations and
a 150 ms target. Pipeline cases use two launches, five warmups, 12 measured
iterations and a 300 ms target. Groups contain 10 or 1,000 requests; the public
matrix covers success, observed handler failure, partial cancellation, capacity
waiting, and mixed single/group submissions. See the
[shared-completion setup](../shared-completion-source/README.md) for its gates,
partition topology, and validation. KB means 1,024 bytes.

Run from each source snapshot's own directory, sequentially with no competing
benchmarks. Copy the same new primitive benchmark into the baseline checkout
before building it. The first campaign runs before then after:

```bash
dotnet run --project tests/RequestBatcher.Benchmarks/RequestBatcher.Benchmarks.csproj \
  --configuration Release -- \
  --filter '*BatchErrorAggregationBenchmarks*' --artifacts /private/tmp/error-accounting-micro

dotnet run --project tests/RequestBatcher.Benchmarks/RequestBatcher.Benchmarks.csproj \
  --configuration Release --no-build -- \
  --filter '*ExplicitCompletionBenchmarks*' --artifacts /private/tmp/error-accounting-public
```

Repeat the following after then before to check the initially noisy cases:

```bash
dotnet run --project tests/RequestBatcher.Benchmarks/RequestBatcher.Benchmarks.csproj \
  --configuration Release --no-build -- \
  --filter '*MixedSinglesAndExplicitGroupAsync*' '*ExplicitGroupPartialCancellationAsync*' \
  --artifacts /private/tmp/error-accounting-repeat
```
