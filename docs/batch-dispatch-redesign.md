# Parallel Batch Dispatch Redesign

## Scope

This document defines the current queue topology, dispatch flow, lifecycle, and
validation contract. Queue partitioning supports batching and key affinity;
handler concurrency is controlled independently by a global execution window.

## Goals

- Bound handler-batch concurrency by `MaxConcurrency` without creating the
  same number of BufferQueue partitions.
- Do not pull a BufferQueue batch when no handler execution slot is available.
- Do not add an application-owned handoff queue between BufferQueue and handler
  execution.
- Permit work from any readable partition to use any free execution slot.
- Keep accepted request completion, caller-cancellation, failure, and shutdown
  semantics exact.
- Treat `MaxPendingRequests` as queue-resident capacity, rather than a combined
  count of queued and executing requests.

## Ordering and Queue Progress

RequestBatcher does not promise request ordering. This includes
`MaxConcurrency = 1`: concurrent callers have no stable append order, and a
partition key is a routing hint only. `UsePartitionKey` continues to select a
deterministic BufferQueue partition for equal keys, but it does not guarantee
serial handler execution or per-key ordering.

The queue uses BufferQueue auto-commit. Once a batch has been pulled, it is no
longer queue-resident and cannot be replayed by the queue. RequestBatcher still
finishes every accepted request exactly once from the handler result, caller
cancellation before dispatch, or an infrastructure failure.

## Topology

At service registration, RequestBatcher resolves a fixed partition count:

```text
effectivePartitionCount = PartitionKey is configured
    ? PartitionKey.PartitionCount
    : 1
```

`UsePartitionKey(selector, partitionCount)` requires `partitionCount` to be
greater than one. The count is fixed for the coordinator lifetime; changing
configuration at runtime does not reshape a live BufferQueue topic. A keyed
partition count is independent of `MaxConcurrency` and process-visible CPU
count so I/O-bound services can choose enough key-local queues for their
workload.

BufferQueue has `effectivePartitionCount` memory partitions. RequestBatcher
creates one pull consumer for the group, allowing that consumer to own all
partitions. BufferQueue chooses the readable partition for each pull, which
lets one global execution window balance work across partitions instead of
reserving one handler task per partition.

```text
producers -> BufferQueue P partitions -> BatchDispatchLoop -> H handler batches
                                         (only pulls with a free slot)

P = effectivePartitionCount
H = MaxConcurrency
```

Without a partition key, single requests and explicit submissions use one queue
partition so adjacent queued items are not split solely for round-robin routing.
That partition can supply multiple concurrently executing handler batches, up to
`MaxConcurrency`. There is no collection window or requirement to fill a batch.
With a numeric or string partition key, equal keys route consistently through
the configured partition topology; separate batches for a key may execute
concurrently. A partition count greater than one is only available together
with a key selector. Without a key selector, all requests use one partition.
Both request-only and request/response registration use the same policy.

The selected keyed partition count changes batch composition and locality. It
does not promise higher throughput, ordering, or one invocation per explicit
submission. Public capacity limits, cancellation boundaries, and lifecycle
behavior remain unchanged.

`BatchSize` remains the BufferQueue pull batch size and the maximum number of
items passed to one handler invocation. It is not multiplied by `H`.

## Queue Dependency Contract

The pipeline uses BufferQueue 2.0.3 with its memory storage. The single pull
consumer scans assigned partitions in a ring and resumes after the partition
that supplied the last batch. Notifications wake the consumer without giving
the notifying partition priority. This balances pull opportunities between
readable partitions; it does not guarantee equal processing time, per-key
fairness, or handler execution order.

String partition keys are routed using XXH3-64 over the complete UTF-16 key on
the supported little-endian .NET targets. BufferQueue supplies this through its
transitive System.IO.Hashing 10.0.9 dependency. Equal keys retain partition
affinity; shared prefixes do not force otherwise different keys together.
Numeric keys retain their existing mapping within the selected topology.
Exact partition assignments are internal and are not stable across dependency
versions. Handler batch composition and batch-level deduplication opportunities
may therefore change when the dependency changes.

No public API or target framework changes are required. RequestBatcher uses no
persistent queue storage: pending requests cannot be recovered after process
failure, and there are no stored partition assignments to migrate. Cancellation,
capacity, exception fan-out, and shutdown retain the lifecycle contract below.

Dependency integration tests must verify balanced pulls across sparsely readable
partitions in both commit modes and full-string routing through RequestBatcher's
key adapter for single-item and explicit batch production. Existing coordinator
tests continue to verify that equal-key batches can execute concurrently. The
complete unit-test project, Release solution build, formatting verification, and
package creation validate the dependency upgrade.

## BatchDispatchLoop

`BatchDispatchLoop` is the queue-facing scheduling loop. Its implementation
uses a semaphore with `H` permits and an in-flight completion tracker.

1. Wait for one execution permit.
2. Advance the BufferQueue async enumerator once. The consumer has
   `AutoCommit = true`, so this succeeds only after queue progress has advanced.
3. Hand BufferQueue's memory-topic `IReadOnlyList` snapshot directly to an
   execution task. An arbitrary enumerable is materialized only as a defensive
   fallback before that handoff.
4. Invoke the handler-batch async method on the dispatch call path. Return to
   step 1 when it completes or reaches its first incomplete asynchronous wait.
5. The execution task completes each request from the handler outcome, removes
   itself from the in-flight tracker, and releases its permit in `finally`.

When `RequestBatchConsumer.ExecuteAsync` returns a synchronously completed
`ValueTask`, the dispatch loop completes the execution inline and releases the
permit without creating an additional async `Task` wrapper. An incomplete
`ValueTask` uses the asynchronous completion path. Both paths keep the same
exactly-once request completion, in-flight tracking, failure propagation, and
permit-release behavior.

There is no `Task.WhenAll` in the normal dispatch path and no per-superbatch
barrier. A handler suspended on an incomplete asynchronous wait holds only its
own execution permit; batches already running in other slots complete normally.
Synchronous work before that suspension, including handler construction and
scope resolution, delays the next pull even if other execution slots are free.
The dispatch loop does not offload each handler to the ThreadPool. The in-flight tracker is used only to
observe terminal drain and task failures without creating a second queue.

The coordinator keeps accepted requests in a concurrent pending registry so
terminal request completion does not take the lifecycle lock for every item.
Admission and stop transitions still use the lifecycle lock. A finished item
is removed from the registry and decrements the pending count atomically; only
the transition from one pending item to zero re-enters the lifecycle lock to
capture and signal the current drain source. Consumer-failure snapshots may
observe requests that finish concurrently, and each request's terminal state
still prevents duplicate completion or capacity release.

Each coordinator creates one bound `OnRequestFinished` delegate before consumer
startup and keeps it in a readonly field. Single and explicit-group admission
pass that same delegate to every pending item. The callback remains specific to
its owning coordinator; it removes only that coordinator's pending state and
signals its drain source. Pending items retain their exactly-once finish guard,
so sharing the delegate does not share item state or change cancellation,
capacity release, partition routing, or handler ordering. Public APIs and
lifetime contracts are unchanged, and no task or pending object is pooled.

Each pending item retains its request, completion, finish callback, and
cancellation registration, but does not retain a second copy of the caller's
`CancellationToken`. The registration uses
`CancellationToken.UnsafeRegister(Action<object?, CancellationToken>, object?)`,
so the cancellation callback receives the original token supplied by the caller.
This also covers the synchronous callback made while registering an already-
canceled token. Cancellation still changes only queued items; after
`TryStartProcessing` succeeds, the handler's terminal result remains authoritative.
The existing state transitions, exactly-once completion, and capacity release
contracts are unchanged.

Direct pending-item tests cover synchronous registration of an already-canceled
token, queued cancellation with one finish notification, and cancellation after
dispatch with both successful and failed handler outcomes.

Validation uses the existing success, failure, cancellation, consumer-failure,
and concurrent disposal regressions. Identical before/after public-pipeline
benchmarks cover singles, explicit groups of different sizes, mixed submissions,
and partition routing; allocation and timing benefits are assessed separately.

Each explicit submission owns a `BatchSubmissionCompletion` that derives from
`TaskCompletionSource` and implements the request-completion contract. Its task
uses `RunContinuationsAsynchronously`; all pending items in the submitted group
share it. The object tracks remaining items, caller cancellation, and distinct
exception references. The final item publishes failure before cancellation
before success. It never publishes while other items remain outstanding. This
avoids a separate TCS wrapper allocation per group without changing public APIs,
per-item routing, terminal guards, capacity handling, or response collection.
There is no task pooling. Existing completion, partial-cancellation, producer
failure, and lifecycle regressions apply to both supported target frameworks.
Primitive allocation measurements attribute the fixed per-group saving, while
identical public-pipeline benchmarks compare small/large groups, success,
failure, partial cancellation, capacity waits, and mixed single/group workloads.

The permit is acquired before advancing the BufferQueue enumerator. With all
handler slots occupied, the loop does not consume more work. The bounded
BufferQueue then holds up to `MaxPendingRequests` queued requests and applies
the configured wait/fail admission behavior directly.

## Lifecycle and Errors

- A request cancelled before dispatch is finished as cancelled and is never
  passed to the handler.
- Once a batch starts handler dispatch, caller cancellation does not replace the
  actual handler result.
- A handler exception completes every active request in that batch with the
  same exception, releases its slot, and does not terminate the dispatch loop.
- An enumeration, auto-commit, or other queue infrastructure failure terminates
  the loop. The coordinator fails every queued request, stops admission, and
  records the failure for `DisposeAsync` to report after cleanup.
- `DisposeAsync` first stops admission, waits until all accepted requests have a
  terminal result, then cancels the dispatch loop. A loop blocked waiting for an
  execution permit observes this cancellation. Running handlers are allowed to
  report their actual outcome before shutdown completes. Disposal then waits for
  consumer supervision to finish and releases coordinator-owned resources.
- `DisposeAsync` is the sole public shutdown entry point. Concurrent and repeated
  calls share one task, including its failure. DI owns disposal in registered
  applications; no separate stop or restart operation is exposed.
- A consumer failure immediately stops admission and fails queued work. Resource
  cleanup runs when the owner disposes the coordinator.
- Callers use async disposal for shutdown; there is no separate cancellable
  shutdown-wait API. Submission signatures and processing semantics are unchanged.
- Tests cover concurrent/repeated disposal, rejection after disposal begins,
  draining active and capacity-waiting submissions, and failure cleanup.

## Tests

The implementation must add or adjust deterministic tests for:

- one global consumer and auto-commit configuration;
- one partition without a key for all concurrency inputs;
- explicit keyed partition-count configuration independent of process CPU count;
- unkeyed requests forming one preloaded batch across multiple execution slots;
- multiple batches from the single unkeyed partition executing concurrently;
- request/response registration preserving the same partition policy;
- no additional pull while all execution slots are occupied;
- a slow batch not blocking a later batch that can use a different free slot;
- queue backpressure while every execution slot is occupied;
- handler failure, enumeration failure, cancellation, and stop/drain behavior;
- removal of ordering guarantees from the public contract and tests.

## Evaluation Evidence

The [BufferQueue 2.0.3 evaluation](evaluations/bufferqueue-2.0.3/README.md) compares
partition counts and handler offloading with controlled in-memory workloads.
These measurements are evidence for tradeoffs, not throughput guarantees.

## Acceptance Criteria

- The topology follows the partition formula and creates exactly one pull
  consumer.
- No batch is pulled while the execution window is full.
- No internal application queue or superbatch `Task.WhenAll` joins handler
  dispatches.
- Public documentation describes the revised ordering and capacity semantics.
- Unit tests cover the stated lifecycle and concurrency contracts.
- Deterministic tests verify the selected topology and batch execution contracts.
