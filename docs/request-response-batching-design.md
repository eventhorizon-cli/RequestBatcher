# Request/Response Batching Design

## Status

Approved implementation design. This document records the request/response
batching contract and the internal adaptation direction before code changes.

## Context

`IRequestBatcher<TRequest>` and `IRequestBatchHandler<TRequest>` are the
existing no-response API. A new request/response API must return one result for
each accepted request without creating a second queue, dispatcher, lifecycle,
or backpressure implementation.

The existing coordinator, pending-request producer, BufferQueue topic, and
dispatch loop intentionally remain the execution core. They are generic over a
request type and have no response-specific behavior. For the request/response
API, `RequestBatchItem<TRequest, TResponse>` becomes that existing request
type. The old no-response handler interface is therefore adapted internally to
forward each batch of items to the new response-bearing handler.

## Public Contract

The existing API remains source and behavior compatible:

```csharp
public interface IRequestBatcher<TRequest>
{
    Task ProcessAsync(TRequest request, CancellationToken cancellationToken = default);

    Task ProcessAsync(
        IEnumerable<TRequest> requests,
        CancellationToken cancellationToken = default);
}
```

The response API is first-class and has matching single and explicit-batch
submission methods:

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

Handlers receive `RequestBatchItem<TRequest, TResponse>`, which exposes the
request and provides `SetResponse(TResponse)`. A handler must set exactly one
response for every item in a successfully completed handler batch. The
`SetResponses(IEnumerable<TResponse>)` extension is an ordered convenience
method: the first enumerated response is assigned to the first item, and so
on. It validates the complete sequence before changing any item.

## Internal Adaptation

The legacy execution pipeline stays unchanged. Its generic `TRequest` is the
physical item carried by pending requests, BufferQueue, and `BatchDispatchLoop`.
For a response registration, that physical request is
`RequestBatchItem<TRequest, TResponse>`.

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
        v
IRequestBatchHandler<RequestBatchItem<TRequest, TResponse>>
        |
        v
ResponseRequestBatchHandler<TRequest, TResponse>
        |
        v
IRequestBatchHandler<TRequest, TResponse>
```

`ResponseRequestBatchHandler<TRequest, TResponse>` implements the original
no-response handler interface whose request type is `RequestBatchItem<TRequest,
TResponse>`. It forwards the batch directly to
`IRequestBatchHandler<TRequest, TResponse>`, then verifies that every item has
one response before returning successfully. That preserves the original
handler-success/failure completion rules without duplicating scheduler code.

The legacy no-response registration keeps its existing physical request type:

```text
IRequestBatcher<TRequest>
        |
        v
RequestBatchCoordinator<TRequest>
        |
        v
IRequestBatchHandler<TRequest>
```

There is no private `NoResponse` response marker, no second coordinator, and
no second dispatch implementation. The existing no-response coordinator runs
unchanged for legacy registrations; response registrations reuse it with
`RequestBatchItem<TRequest, TResponse>` as its request payload.

## Registration and Scoping

Each `AddRequestBatcher` call registers exactly one logical pipeline for a
request type. Registering both the legacy and response-bearing forms for the
same `TRequest` remains invalid because their queue topic and pending-capacity
settings would otherwise conflict.

For singleton handlers, the response handler is resolved once when the
coordinator is composed. For scoped and transient handlers, a fresh async scope
is created for each dispatched batch and the handler is resolved inside that
scope. `ResponseRequestBatchHandler<TRequest, TResponse>` forwards into that
scoped response handler as part of the original handler invocation.

`UsePartitionKey` is configured from the original `TRequest` and projected to
`RequestBatchItem<TRequest, TResponse>.Request` before the BufferQueue topic is
created. This preserves routing behavior without exposing queue details in the
public response API.

The response registration invokes its configuration callback once and registers
the validated `RequestBatchOptions<TRequest>` snapshot as
`IOptions<RequestBatchOptions<TRequest>>`, just as the legacy registration
does. A projected internal snapshot configures the item-based queue.

## Completion and Lifecycle

- A response call completes only after its batch handler has completed and its
  item has exactly one assigned response.
- A missing response is a handler contract violation. Every active request in
  that handler batch faults with the same failure.
- Duplicate assignment is a handler contract violation and preserves the first
  assigned value.
- Caller cancellation can remove a request only before dispatch. Once a batch
  starts, completion reports the handler's actual success or failure.
- A legacy no-response call keeps its existing completion behavior.
- `StopAsync` and `DisposeAsync` on the coordinator continue to stop admission
  before draining accepted work. The response facade is backed by that same
  coordinator and is disposed with it by the service provider.

## Compatibility and Scope

No existing public API is removed or changed. `RequestBatchCoordinator<TRequest>`
remains the execution coordinator for both registrations, instantiated with
`RequestBatchItem<TRequest, TResponse>` only for the response registration.
BufferQueue topics, partitions, producers, consumers, and handler adapters
remain implementation details.

This change does not alter scheduling, partition-count calculation,
backpressure, ordering, or durability contracts. It changes only the internal
completion representation so both public API families reuse the same pipeline.

## Test Plan

The implementation must prove all of the following deterministically:

- a response handler returns single and batch results in submission order;
- missing and duplicate responses fault the affected handler batch;
- `SetResponses(IEnumerable<TResponse>)` preserves ordered mapping and makes
  no partial changes when enumeration or count validation fails;
- a response handler is invoked through the existing no-response coordinator
  pipeline and keeps its existing success, failure, cancellation, and shutdown
  behavior;
- response and legacy registrations for the same request type are rejected;
- scoped response and legacy handlers are resolved once per dispatched batch;
- partition-key projection still routes using the original request value.
