# Request/Response Batching Design

## Status

Implementation design for the current request/response batching API.

## Goals

- Return one typed response for every successfully processed request.
- Reuse the same coordinator, BufferQueue topic, admission control, dispatch,
  cancellation, failure, and shutdown implementation as request-only batching.
- Keep BufferQueue and response completion details out of the caller-facing API.
- Resolve each handler according to the lifetime supplied at registration.

## Public API

Request-only callers submit one request or an explicit request sequence:

```csharp
public interface IRequestBatcher<TRequest>
{
    Task ProcessAsync(
        TRequest request,
        CancellationToken cancellationToken = default);

    Task ProcessAsync(
        IEnumerable<TRequest> requests,
        CancellationToken cancellationToken = default);
}
```

Request/response callers use the corresponding response-bearing interface:

```csharp
public interface IRequestBatcher<TRequest, TResponse>
{
    Task<TResponse> ProcessAsync(
        TRequest request,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TResponse>> ProcessAsync(
        IEnumerable<TRequest> requests,
        CancellationToken cancellationToken = default);
}
```

Handlers are application service types:

```csharp
public interface IRequestBatchHandler<TRequest>
{
    ValueTask HandleAsync(
        IReadOnlyList<TRequest> requests,
        CancellationToken cancellationToken = default);
}

public interface IRequestBatchHandler<TRequest, TResponse>
{
    ValueTask HandleAsync(
        IReadOnlyList<RequestBatchItem<TRequest, TResponse>> entries,
        CancellationToken cancellationToken = default);
}
```

Registration names the handler type and its lifetime explicitly:

```csharp
services.AddRequestBatcher<TRequest, THandler>(
    handlerLifetime,
    configure);

services.AddRequestBatcher<TRequest, TResponse, THandler>(
    handlerLifetime,
    configure);
```

One logical pipeline may be registered for a request type. Registering both
forms for the same `TRequest` is invalid because their queue topology and
capacity configuration would conflict. A rejected registration does not invoke
its configuration callback or modify the service descriptors and options of the
existing pipeline.

## Compatibility

The public registration surface is type-based: applications provide an
`IRequestBatchHandler` implementation and choose its service lifetime when
registering the batcher. Request-only and request/response pipelines retain
their respective submission contracts shown above.

Response-handler parameters and response-assignment extension receivers are named
`entries`. Static calls using named arguments must use `entries:`; null receiver
validation reports `entries` as the parameter name. Public types and positional
calls retain the same contract.

## Response Items

`RequestBatchItem<TRequest, TResponse>` is the physical request type carried by
the response pipeline. It exposes the submitted `Request` and one response
slot.

Variables and parameters use `entry` / `entries` for these wrappers and
`request` / `requests` for the original `TRequest` values. The public wrapper type
remains `RequestBatchItem<TRequest, TResponse>`. This naming convention does not
change routing, response assignment, or the completion flow described below.

The handler sets a response with `SetResponse(TResponse)`. The response slot
accepts exactly one assignment. A second assignment throws and preserves the
first value.

`SetResponses(IEnumerable<TResponse>)` assigns a complete ordered result set:

- the first response maps to the first item, the second to the second item,
  and so on;
- the response count must equal the item count;
- the response sequence is fully materialized and validated before any item is
  changed;
- an enumeration failure, count mismatch, or existing assignment leaves all
  previously unset items unchanged.

After a response handler completes successfully, every item is validated. An
unset item fails its handler batch.

## Internal Flow

The response facade creates one item for every caller request and submits those
items through the request-only batching pipeline:

```text
IRequestBatcher<TRequest, TResponse>
        |
        v
ResponseRequestBatcher<TRequest, TResponse>
        |
        v
IRequestBatcher<RequestBatchItem<TRequest, TResponse>>
        |
        v
RequestBatchCoordinator<RequestBatchItem<TRequest, TResponse>>
        |
        +--> PendingRequestProducer
        +--> BufferQueue
        +--> BatchDispatchLoop
        |
        v
IRequestBatchHandler<RequestBatchItem<TRequest, TResponse>>
        |
        v
ResponseRequestBatchHandler<TRequest, TResponse>
        |
        v
IRequestBatchHandler<TRequest, TResponse>
```

`ResponseRequestBatchHandler<TRequest, TResponse>` is the internal adapter. It
forwards the item batch to the application handler and validates every response
slot before the coordinator marks the physical requests successful.

The explicit-batch facade retains its item sequence until processing finishes,
then reads responses in that same sequence. The returned
`IReadOnlyList<TResponse>` therefore follows caller input order even when queue
routing splits items across partitions or handler invocations.

## Configuration and Routing

The configuration callback produces one validated
`RequestBatchOptions<TRequest>` snapshot. That snapshot is registered for
application inspection and projected to
`RequestBatchOptions<RequestBatchItem<TRequest, TResponse>>` for the internal
queue.

`UsePartitionKey(selector, partitionCount)` always receives the original
`TRequest`. The projected queue selector reads `RequestBatchItem.Request`
before calculating the key and preserves the configured partition count. Equal
keys route to the same queue partition but do not guarantee serialized handler
execution or ordering. The partition count must be greater than one and is
independent of `MaxConcurrency` and process CPU count.

## Typed Single-Request Completion

Single response submissions create an internal `ResponseCompletion<TRequest,
TResponse>` result box that inherits `TaskCompletionSource<TResponse>`. It
references the response item and implements the pending-request completion
contract. Pending state uses this box instead of an untyped completion source.
The facade returns its typed task directly when queue production completes
synchronously. If production must wait, the existing producer observation path
is awaited before returning the response; enqueue failure and cancellation
remain observed.

`SetResponse` only stores a value. Successful completion is published after the
handler, response validation for the entire handler batch, and async scope
cleanup all succeed. Pre-dispatch cancellation, queue failures, and handler
failures complete the same box. An `OperationCanceledException` retains the
canceled-task behavior and cancellation token of the response facade's async
wait. A single response task uses standard TCS cancellation: awaiting it throws
`TaskCanceledException`, rather than preserving a handler's original cancellation
exception instance, message, or derived type. Non-cancellation failures retain
the original exception instance. Continuations use
`RunContinuationsAsynchronously`; result boxes and their tasks are never pooled.

Explicit submissions still use one shared batch completion, with responses
collected in input order. Pending state calls shared-batch completion methods
directly, avoiding interface dispatch for every item in an explicit submission.
Request-only submissions retain one untyped source.
There are no public API, partition routing, capacity, or lifetime changes.
The cancellation-exception normalization above is an observable behavior change. Regression tests cover delayed success, response set then
failure, missing responses, cancellation before/after dispatch, backpressure,
queue rejection, and disposal. Public-pipeline benchmarks compare the same
source before and after this change, including real waiting continuations,
failures that are observed, and request-only/explicit-batch controls.

## Handler Lifetime

The handler type is registered in the application-owned service collection
with the supplied `ServiceLifetime`.

- A singleton handler is resolved when the singleton coordinator is composed.
- A scoped or transient handler is resolved in a new async scope owned by one
  dispatched handler batch.
- That async scope is disposed after the handler batch completes.

No nested service provider is created.

## Completion, Cancellation, and Shutdown

- A caller completes successfully only after its handler batch succeeds and
  its response slot contains a value. A null response is valid.
- A non-cancellation handler exception faults every active request in that
  handler batch with the same exception. Response tasks represent a handler
  `OperationCanceledException` as cancellation; single responses preserve its
  token rather than its exception identity.
- Caller cancellation removes work only before handler dispatch.
- Once dispatch begins, the caller observes the actual handler outcome.
- `DisposeAsync` stops admission, drains accepted work, stops dispatch, and
  releases coordinator-owned resources. Concurrent and repeated calls share
  the same disposal task. The application container owns async disposal.
- Pending work is in-process and is not recovered after process failure.

## Validation

The naming convention is checked by compiling the library, sample, and benchmark
projects and running the existing response and full test suites on both target
frameworks; it adds no runtime behavior.

Deterministic tests cover:

- single and explicit-batch response ordering;
- missing and duplicate response assignment;
- ordered bulk assignment and all-or-nothing validation;
- handler failure and caller cancellation;
- stop and drain behavior;
- handler registration and per-batch scope ownership;
- duplicate logical-pipeline registration;
- partition-key projection from the original request;
- public and projected option snapshots.
