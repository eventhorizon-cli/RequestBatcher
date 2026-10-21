# Observability

## Contract

RequestBatcher exposes the `RequestBatcher` ActivitySource. An optional
`RequestBatcher.OpenTelemetry` package supplies
`TracerProviderBuilder.AddRequestBatcherInstrumentation()`, registering that
source with the application's provider. The core package uses only .NET tracing
APIs; it neither creates an OpenTelemetry provider nor selects exporters or
sampling policy. The adapter depends on OpenTelemetry.Api.ProviderBuilderExtensions
1.20.1 and supports the same net8.0/net10.0 targets.

Each public submission creates at most one `RequestBatcher.Execute` Internal
span under the caller's current Activity. Its duration covers synchronous
submission preparation, capacity waiting, queue residence, handler execution,
handler scope disposal, and response materialization until the returned task's
outcome is available. Delegate binding is inside that boundary. Empty,
pre-canceled, rejected, and failed submissions terminate their call span too.
Argument-null validation that throws before a submission begins remains synchronous.
An explicit submission has one call span even when it becomes multiple batches.

Each dispatched nonempty batch creates one `RequestBatcher.ProcessBatch`
Consumer span in the isolated background context, with its own root trace.
It links to the distinct submission contexts of requests that actually entered
processing. These are span-context links, deduplicated by TraceId and SpanId;
one batch can link to many calls and one call can be linked by many batches.
Canceled queued requests are omitted. Database and HTTP instrumentation inside
the handler become children of the batch span. Scoped-handler construction and
async disposal occur inside the batch span. A batch span ends before publishing
its requests' terminal results so a completed call has a completed batch trace.

The call span measures end-to-end waiting; the Consumer span measures execution.
The call span is not a messaging send span, whose duration would imply enqueue
latency only. RequestBatcher remains in-process and cannot recover pending work
after process failure. Links model causality, not shared trace IDs or ordering.

## Context and outcomes

Only ActivityContext is transported. ExecutionContext, Activity instances,
logging scopes, and baggage are not copied into the consumer. When a listener
drops a call Activity, an available caller context can still provide a link.
No valid context means no link. Links and batch attributes are supplied at span
creation, so samplers can inspect them. The application's sampler governs batch
root sampling; parent-based sampling does not automatically follow links.

Span names are fixed. Both call and batch spans include `requestbatcher.name` for named
registrations; unnamed registrations omit it. The name is the exact public DI registration
name, never an internal topic or pipeline key. It is available at Activity creation for
sampling and carried once on the coordinator through dispatch and response/delegate adapters,
without adding per-request state. Tags also use `requestbatcher.request.type`,
`requestbatcher.batch.size`, and `requestbatcher.canceled`. Faults set Error and
`error.type`; payloads, tenant keys, exception messages and stacks are not emitted.
Expected task cancellation sets the cancellation tag without Error; a faulted
task containing OperationCanceledException remains a fault. Tracing preserves
original exception references, aggregate failures, cancellation tokens, and task
terminal state. The caller's current Activity is restored before ExecuteAsync
returns its task. Handler failures retain the existing fan-out semantics.

All distinct contexts are supplied as links; SDK/exporter link limits may drop
links for large batches. Applications needing complete large-batch correlation
must configure their provider's link limit. Tracing is best effort when sampling
or backend retention drops either endpoint. The library does not force recording.

## Internal flow and performance

Diagnostics owns a shared ActivitySource and tracing helpers. Public entry points
check HasListeners before creating closures or tracing state. With no listener,
the existing task path is used without an async wrapper, span, link collection,
or extra per-item tracing allocation. Traced pending requests use an internal
subclass that stores context, keeping the untraced pending object's size unchanged.
Tracing-enabled calls retain only context on queued items; request arrays,
completion contracts, routing, cancellation, and lifecycle stay unchanged.

An enabled call starts its Activity, invokes the normal core path, and restores
caller ambient context synchronously. Already completed tasks are returned
unchanged after ending the span. Incomplete recorded calls use a completion
bridge that ends the span before publishing the same success, cancellation, or
original fault collection. No Task.Run or extra thread is added per call/batch.
Response and delegate adapters invoke uninstrumented core methods under their
outer call span to avoid duplicate spans and include their own preparation.

## Validation and compatibility

Public batching APIs and handler lifetimes remain unchanged. Tests use real DI,
ActivityListener, and the OpenTelemetry SDK without external services. Cover
multiple callers in one batch, a call split across batches, parent/context
restoration, downstream children, scope disposal and response materialization,
queue/capacity waiting, cancellation before/after dispatch, faults (including
faulted OperationCanceledException), duplicate links, no-context calls, rejected
and empty submissions, sampled-out tracing, named/delegate/response forms, and
adapter registration/null validation. Named and unnamed class/delegate registrations,
with and without responses, must expose the correct name on both spans without mixing
concurrent pipelines. Tests with global listeners are isolated
from unrelated Activity-sensitive tests.

Matched measurements compare the previous release implementation plus context
isolation with the new implementation without listeners, and compare disabled,
dropped, and recorded tracing for single and explicit submissions. Validate
processed counts, report time and allocations, and do not claim zero overhead.
README guidance and simplified SVG diagrams explain lifetime and correlation.
Both NuGet packages are built, packaged, and published in the same release.
