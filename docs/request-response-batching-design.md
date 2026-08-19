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
        IReadOnlyList<RequestBatchItem<TRequest, TResponse>> requests,
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
capacity configuration would conflict.

## Compatibility

The public registration surface is type-based: applications provide an
`IRequestBatchHandler` implementation and choose its service lifetime when
registering the batcher. Request-only and request/response pipelines retain
their respective submission contracts shown above.

## Response Items

`RequestBatchItem<TRequest, TResponse>` is the physical request type carried by
the response pipeline. It exposes the submitted `Request` and one response
slot.

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

`UsePartitionKey` always receives the original `TRequest`. The projected queue
selector reads `RequestBatchItem.Request` before calculating the key. Equal
keys route to the same queue partition but do not guarantee serialized handler
execution or ordering.

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
- A handler exception faults every active request in that handler batch with
  the same exception.
- Caller cancellation removes work only before handler dispatch.
- Once dispatch begins, the caller observes the actual handler outcome.
- `StopAsync` stops admission, drains accepted work, and then stops dispatch.
- `DisposeAsync` follows the same drain path and releases coordinator-owned
  resources.
- Pending work is in-process and is not recovered after process failure.

## Validation

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
