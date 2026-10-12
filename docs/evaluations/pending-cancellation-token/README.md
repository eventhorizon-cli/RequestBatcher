# Pending request cancellation-token storage

Each pending item receives the caller's original token through
`CancellationToken.UnsafeRegister(Action<object?, CancellationToken>, object?)`
instead of retaining a duplicate token field. The callback still cancels only
queued work. Synchronous registration callbacks for an already-canceled token
also receive the correct token, without reading an unassigned registration.
After handler dispatch, the handler outcome remains authoritative. Completion,
capacity release, task ownership, and partition routing are unchanged.

## Result

Retain this as a measured **8 B per pending item** allocation reduction on the
measured Arm64 runtime. The primitive falls from **160 B to 152 B**, both with
and without a cancellable token. It includes the actual pending object and its
single-request completion/task; the task itself is not removed or pooled.

For a 1,000-item successful explicit group, full-pipeline allocation falls from
181.56 KB to 173.75 KB initially and to 173.74 KB in the repeat: about **7.81 KB**
less per submission, or **4.3%** of this workload's allocation. This saving is
additional to the fixed 24 B/group shared-completion optimization already
present in both versions. Capacity and mixed workloads submit approximately
twice as many items and save approximately twice as many bytes in large groups.

No throughput gain is claimed. Initial public-pipeline timings were worse;
a complete reverse-order campaign did not reproduce those regressions. The
baseline itself changed substantially between campaigns. The measurements do
not establish a stable material regression, but do not prove equal throughput
or exclude a small regression. All timing results are retained below.

## Primitive allocation

| Campaign | Cancellable | Before | After | Allocated before / after |
|---|---|---:|---:|---:|
| Initial, before then after | No | 24.55 ns | 23.91 ns | 160 / 152 B |
| Initial, before then after | Yes | 43.11 ns | 43.69 ns | 160 / 152 B |
| Repeat, after then before | No | 26.03 ns | 24.15 ns | 160 / 152 B |
| Repeat, after then before | Yes | 47.29 ns | 43.94 ns | 160 / 152 B |

A cancellation source is created outside measurement and reused. Each measured
operation constructs a real `PendingBatchRequest<int>`, starts it, completes it,
and returns its completed task. Registrations are disposed by normal completion;
the reusable cancellation source may reuse registration nodes. This isolates the
pending-object saving, not the cost of simultaneously keeping many registrations
alive. The public partial-cancellation workload covers that separate path.

## Public pipeline

Initial full campaign:

| Scenario | Items/group | Before | After | Time change | Before allocated | After allocated |
|---|---:|---:|---:|---:|---:|---:|
| Explicit group observed handler failure | 10 | 232.187 μs | 237.643 μs | +2.35% | 10.58 KB | 10.51 KB |
| Explicit group partial caller cancellation | 10 | 43.168 μs | 48.239 μs | +11.75% | 8.34 KB | 8.25 KB |
| Explicit group success | 10 | 8.501 μs | 9.444 μs | +11.09% | 5.09 KB | 5.01 KB |
| Explicit groups with capacity waiting | 10 | 22.501 μs | 23.289 μs | +3.50% | 8.56 KB | 8.39 KB |
| Mixed single requests and one explicit group | 10 | 24.931 μs | 29.185 μs | +17.06% | 11.34 KB | 11.21 KB |
| Explicit group observed handler failure | 1000 | 1,707.274 μs | 1,788.869 μs | +4.78% | 214.99 KB | 207.18 KB |
| Explicit group partial caller cancellation | 1000 | 580.120 μs | 626.802 μs | +8.05% | 471.98 KB | 464.18 KB |
| Explicit group success | 1000 | 158.156 μs | 178.296 μs | +12.73% | 181.56 KB | 173.75 KB |
| Explicit groups with capacity waiting | 1000 | 341.444 μs | 361.768 μs | +5.95% | 365.92 KB | 350.29 KB |
| Mixed single requests and one explicit group | 1000 | 1,022.357 μs | 1,039.094 μs | +1.64% | 467.34 KB | 451.28 KB |

Success with 10/1,000 items, small mixed submissions, and small partial cancellation
were initially 11–17% slower. All twelve benchmark cases were repeated in reverse
version order, with unchanged sources and settings:

| Scenario | Items/group | Before | After | Time change | Before allocated | After allocated |
|---|---:|---:|---:|---:|---:|---:|
| Explicit group observed handler failure | 10 | 243.50 μs | 239.301 μs | -1.72% | 10.58 KB | 10.51 KB |
| Explicit group partial caller cancellation | 10 | 46.04 μs | 45.653 μs | -0.84% | 8.34 KB | 8.26 KB |
| Explicit group success | 10 | 10.39 μs | 9.504 μs | -8.53% | 5.09 KB | 5.01 KB |
| Explicit groups with capacity waiting | 10 | 24.21 μs | 21.628 μs | -10.67% | 8.55 KB | 8.38 KB |
| Mixed single requests and one explicit group | 10 | 30.59 μs | 29.823 μs | -2.51% | 11.26 KB | 11.46 KB |
| Explicit group observed handler failure | 1000 | 1,803.84 μs | 1,804.696 μs | +0.05% | 214.99 KB | 207.19 KB |
| Explicit group partial caller cancellation | 1000 | 672.69 μs | 621.704 μs | -7.58% | 471.99 KB | 464.21 KB |
| Explicit group success | 1000 | 188.84 μs | 180.174 μs | -4.59% | 181.56 KB | 173.74 KB |
| Explicit groups with capacity waiting | 1000 | 365.85 μs | 330.163 μs | -9.75% | 365.92 KB | 350.27 KB |
| Mixed single requests and one explicit group | 1000 | 896.25 μs | 873.833 μs | -2.50% | 466.58 KB | 451.33 KB |

The initially slower success cases became 8.5% and 4.6% faster in the repeat;
small mixed submissions became 2.5% faster. The baseline's small success mean
changed from 8.501 to 10.39 μs, and its large success mean from 158.156 to
188.84 μs. This between-campaign variation prevents assigning a dependable
latency change to the field removal. Some asynchronous measurements were
multimodal or had short-iteration warnings. Small mixed allocation also varied
with batch formation: its repeat increased from 11.26 to 11.46 KB despite the
primitive reduction. Large-group allocation and the primitive consistently
show the expected saving; total pipeline allocation includes scheduling and
handler-batch formation and is not fixed solely by item count.

## Method and reproduction

Measured 2026-10-13 on macOS Arm64, .NET SDK 10.0.100, .NET runtime 8.0.25,
BenchmarkDotNet 0.15.8. The baseline source snapshot is `1fe5abd`, whose production
source is identical to main `8874d14`. The measured candidate is `f6fb2ef`,
rebased as `b3a2c6d` without changing production or benchmark source.

Both versions use identical benchmark source:

- `ExplicitCompletionBenchmarks.cs` SHA-256: `6b3deb808ebecf2c852033c51f26033903aeb924474e8ec32dbe3f81bb8f68cb`
- `PendingRequestAllocationBenchmarks.cs` SHA-256: `8a1e7cf5e99b07dc9ce45ec425ce3f15a5597014c56cc07d17a88ca4edeeabb6`
- Candidate `PendingBatchRequest.cs` SHA-256: `1d3be4f0739e136adc7607857d4fc4eb9f401d637ad41130a95dee829a98d105`

Run the versions sequentially, with no concurrent local builds/tests:

```bash
NuGetAudit=false dotnet run \
  --project tests/RequestBatcher.Benchmarks/RequestBatcher.Benchmarks.csproj \
  --configuration Release -- \
  --filter '*PendingRequestAllocationBenchmarks*' '*ExplicitCompletionBenchmarks*' \
  --artifacts /tmp/pending-token-results
```

The primitive uses one launch, three warmups, and eight measured iterations.
The public pipeline uses two launches, five warmups, twelve measured iterations,
and a 300 ms iteration target. The public scenarios, input/gate/counter costs,
partition routing, partial-cancellation drain sentinel, and exactly-once outcome
validation are unchanged from the
[shared completion evaluation](../shared-completion-source/README.md#method-and-reproduction).
This includes groups of 10/1,000 for success, observed handler failure, partial
cancellation, capacity waiting, and mixed single/group submissions.

Raw CSV and Markdown reports are in `before/`, `after/`, `before-repeat/`, and
`after-repeat/`. All 48 measured cases completed without validation failures.
Four new deterministic tests cover construction-time cancellation, queued
cancellation with the original token, and post-dispatch success/failure. Both
pre-dispatch tests verify that processing cannot start and finish notification
occurs only once. All 122 tests passed on each of net8.0 and net10.0, as did
Release solution build, scoped formatting, and packing both target frameworks.
