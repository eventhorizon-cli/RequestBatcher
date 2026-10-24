# Observability

## Contract

RequestBatcher exposes the `RequestBatcher` ActivitySource and meter through built-in .NET APIs. The core package has no
OpenTelemetry package dependency. Applications can register these stable names directly with `AddSource("RequestBatcher")`
and `AddMeter("RequestBatcher")`. The optional `RequestBatcher.OpenTelemetry` package supplies the corresponding
`AddRequestBatcherInstrumentation()` builder extensions. Both packages target net8.0/net10.0 and are versioned independently.
The integration package declares a minimum compatible RequestBatcher dependency and requires
OpenTelemetry >= 1.20.1, without an upper bound or exact-version constraint. It never creates a provider or selects exporters.
The SDK dependency is necessary for its public sampler-decorator hook and remains outside the core package.

Moving the registration extensions into the optional package requires existing users of those extensions to reference
`RequestBatcher.OpenTelemetry` and rebuild. Their namespaces and method names remain the same. The tracing extension
registers the source and decorates Handler batch sampling; direct source registration preserves application sampling.
Users registering source/meter names directly need no additional package. Source and meter names are documented stable
contracts; public instances or constants are not required.

Each public submission creates at most one `RequestBatcher.Execute` Internal
span under the caller's current Activity. Its duration covers synchronous
submission preparation, capacity waiting, queue residence, handler execution,
handler scope disposal, and response materialization until the returned task's
outcome is available. Delegate binding is inside that boundary. Empty,
pre-canceled, rejected, and failed submissions terminate their call span too.
Argument-null validation that throws before a submission begins remains synchronous.
An explicit submission has one call span even when it becomes multiple batches.

With a listener registered, each dispatched nonempty batch attempts to create one `RequestBatcher.Handle`
Consumer span in the isolated background context, with its own root trace.
When at least one participating Execute context has the sampled flag, it links to
all distinct submission contexts of requests that actually entered processing,
including unsampled contexts. When none has the sampled flag, it supplies no
submission links. These are span-context links, deduplicated by TraceId and SpanId;
one batch can link to many calls and one call can be linked by many batches.
Each sampled Execute span that requests data also links back to every batch that processed its requests.
Batch contexts are collected during dispatch and added to Execute before it ends;
these late links are not available to the Execute sampler. Links are deduplicated
per call and batch. The library does not modify the application's parent Activity.
Canceled queued requests are omitted. Database and HTTP instrumentation inside
the handler become children of the batch span. Scoped-handler construction and
async disposal occur inside the batch span. A batch span ends before publishing
its requests' terminal results so a completed call has a completed batch trace.

The call span measures end-to-end waiting; the Consumer span measures execution.
The `Handle` operation names the batch handler invocation, including its scope lifecycle.
Changing this operation name requires updating span-name queries and dashboards;
the ActivitySource name, span kinds, parent relationships, and link targets remain the same.
Named-span tests and live integration checks validate the exact operation name and links.
The call span is not a messaging send span, whose duration would imply enqueue
latency only. RequestBatcher remains in-process and cannot recover pending work
after process failure. Links model causality, not shared trace IDs or ordering.

RequestBatcher also exposes a `RequestBatcher` meter. The
`MeterProviderBuilder.AddRequestBatcherInstrumentation()` extension registers
that meter with the application's provider. The package does not create a
meter provider or choose an exporter. The metrics are deliberately low
cardinality and use only the request type and optional public batcher name as
dimensions:

- `requestbatcher.requests.submitted` counts requests accepted by a coordinator.
- `requestbatcher.requests.processed` counts requests completed by a handler.
- `requestbatcher.batches.processed` counts non-empty dispatched batches.
- `requestbatcher.batch.size` records the number of requests in each batch.
- `requestbatcher.batch.duration` records handler duration in milliseconds.

The request-type dimension uses the public request type's simple name, without
its namespace or an internal response wrapper. The name is cached per closed
coordinator/consumer type. Types with the same simple name share this dimension;
public batcher names can distinguish pipelines. Existing label filters must use
short names. Instrument names remain unchanged and already contain the
`requestbatcher.` prefix; an exporter namespace must not duplicate it.

Batch measurements finish alongside the batch span, before request completions are
published. Processed counts include failed handler attempts and exclude requests
canceled before dispatch. Duration includes handler scope disposal, but excludes
publishing results to callers.

Metrics are emitted only when a meter listener is subscribed. With no listener,
the instruments return immediately and do not allocate metric tags or timing
state. Payload values, tenant identifiers, partition keys, and exception text
are never metric dimensions.

## Context and outcomes

Batch correlation uses ActivityContext and a shared per-submission collection of batch contexts. Handler execution
does not inherit the submitting caller's ExecutionContext, Activity, logging scopes, or baggage. Diagnostics retains
the call Activity and captured ExecutionContext only for the completion notification, restoring that context around
span finalization and restoring the completing thread afterward. When a listener
drops a call Activity, an available caller context can still participate in batch correlation.
No valid context means no link. Links from Handle to participating Execute contexts and creation-time batch
attributes are supplied when Handle starts, so samplers can inspect them. The application's sampler governs batch
root sampling when applications register the source directly; the integration extension decorates that sampler with
linked-batch sampling. Parent-based sampling does not automatically follow links.

### Sampling policy propagation

Core RequestBatcher follows the application's OpenTelemetry SDK sampling configuration and installs no sampler. The
[sampling diagram](diagrams/tracing-sampling.svg) describes using RequestBatcher.OpenTelemetry with its parameterless
tracing extension registered. It shows the package's sampling contract without binding to a particular application sampler, root ratio, or
sample application code. The caller HTTP or business span is Execute's parent; without a caller
span, Execute is a root. Handle has no parent and starts an independent trace. It is the parent of the Handler's DB
and HTTP spans, and HTTP instrumentation propagates that batch context through `traceparent`.
Handle links to the Execute spans whose requests it handles, associating submission with processing. These links
carry Execute trace/span IDs and sampling flags. At least one sampled Execute context enables links to all participating
contexts in that batch, including contexts useful for searching unsampled trace logs. When all Execute contexts are
unsampled, the core skips the batch link collector. This gate does not change the batch's parent. The integration
extension inspects the creation-time link contexts: it samples Handle if at least one Execute
context has the sampled flag and drops Handle otherwise. Without that policy, the application's sampler decides
whether to sample the independent root. Links from Execute to the Handle spans processing its requests
are added at Execute completion and cannot affect its earlier sampling decision. Diagram arrows must explicitly
identify parent-to-child relationships and the Handle-to-Execute
link direction. Do not document a version-dependent SDK default sampler.

Describe concrete sampler configurations separately from the diagram. `ParentBasedSampler` follows a parent's sampled
flag when present and delegates to its configured root sampler only when no parent exists. For example,
`ParentBasedSampler(new AlwaysOnSampler())` samples children of sampled parents, drops children of unsampled parents,
and samples roots. A ratio root sampler does not override a sampled parent's decision. ParentBased does not follow links,
so it applies its root sampler to Handle. The observability sample explicitly uses `AlwaysOnSampler`; the tracing
I/O benchmark configures `ParentBasedSampler(new TraceIdRatioBasedSampler(ratio))` for its sampling matrix without the
linked-batch policy. With the integration extension registered, Handle uses the package's link-based decision instead of the root
sampler's decision. Changes to sample configurations must not require changing the package sampling diagram.

An SDK Drop result may retain a propagation Activity; RequestBatcher keeps its context so Handle can link to it in
a mixed batch, and retains completion state for Activity lifetime. That Execute does not collect links to Handle.
RecordOnly requests all data without the sampled flag and also skips links from Execute to Handle. Only an Execute
with both `Recorded` and `IsAllDataRequested` collects links to the Handle spans processing its requests.
With direct source registration instead of the integration extension, a sampled Execute may still link to an unsampled batch Activity, because
independent batch sampling follows the application's policy. Export eligibility also depends on processors/exporters. If a registered
listener creates no Execute Activity, an available caller context can still participate in correlation under the same
sampled-context batch gate. With no RequestBatcher listener, the ordinary untraced path creates no such link.

This is a correlation compatibility change: all-unsampled batches no longer expose submission links to custom samplers,
and RecordOnly Execute spans no longer collect links to Handle. Mixed batches retain every valid submission context,
not just sampled contexts. Log emission, ExecutionContext isolation, public Tasks, cancellation, and handler execution
remain unchanged. Deterministic tests cover all-unsampled batches, RecordOnly calls, mixed sampling with the sampled
item appearing first or last, duplicate explicit submissions, canceled items, caller-context fallback, and creation-time
sampler inputs. SDK stress tests cover both endpoint sampling rates and mixed batches. The 20 ms tracing I/O benchmark
compares disabled, 0%, 1%, 10%, and 100% sampling against the previous source with exactly-once validation.

Every batch Activity requesting data records `requestbatcher.batch.submission.count`, the number of distinct Execute
submissions represented by the requests that entered that handler invocation. Multiple items from one explicit submission
count once within each handler batch; separate Execute calls count separately even under the same caller trace or when
no Execute Activity exists. Canceled queued items do not contribute. The existing shared submission completion identity
provides this count without adding per-request state. When the number of distinct contexts linked from Handle
equals the item count, every
item is already known to represent a separate submission and that count is reused without a second set. Otherwise,
completion identities provide an exact count, including absent contexts and shared caller-context fallbacks.
Counting runs only after the batch sampler requests data, so dropped
batches allocate no submission-count set. This final span attribute is not a creation-time sampler input. Unlike
`requestbatcher.batch.size`, which counts items, it describes how many submissions were merged. It remains accurate when
links are omitted and is a span attribute only, not a new metric dimension. Tests cover mixed single/explicit submissions,
unsampled and absent contexts, and independent submissions under one parent.

### Integration package sampling

`RequestBatcher.OpenTelemetry` provides `AddRequestBatcherInstrumentation()` on the tracer builder. It registers the
source and uses the SDK's public `ConfigureSampler` callback to decorate the sampler selected by application code or
environment configuration, independent of `SetSampler` registration order. There is no sampling switch or boolean
overload. Repeated registration is idempotent. Applications that want the original sampler to govern batch roots
register `AddSource("RequestBatcher")` directly instead.

The decorator recognizes RequestBatcher Handle roots by the stable operation name (including named registrations),
Consumer kind, empty parent, and existing `requestbatcher.batch.size` and `requestbatcher.request.type` creation tags.
The SDK's sampling parameters do not include ActivitySource, so all these checks are required; other activities delegate
unchanged to the original sampler. No new creation tag is added just to identify this operation.
For a recognized batch it returns RecordAndSample if any creation link has the sampled flag, otherwise Drop.
The batch remains an independent root. Thus sampled Execute spans do not lose their handler to a second ratio decision;
the extension intentionally changes batch sampling only, including when the application's root ratio is zero.
Other spans, including Handler children, still use the original sampler. ParentBased children follow the batch's flag;
arbitrary application samplers can make different decisions. No Activity flags are changed after creation. Suppression,
processor/exporter behavior, and delivery limits still apply, so recording is not a guarantee of backend delivery.
Sampling reads the links supplied as creation parameters before the Handle Activity object is allocated. `Drop` is a
sampling decision, not a guarantee that `StartActivity` returns null: the SDK can retain an unsampled root Activity for
context propagation. Core therefore still calls `StartActivity` when all Execute contexts are unsampled, leaving Activity
creation to the registered listeners and allowing Handler children to observe the unsampled batch context.
The wrapper owns disposal of its original sampler, because the SDK disposes only the final wrapper.

Tests exercise the parameterless extension, sampled and all-unsampled batches, mixed links, named batchers, SDK
registration order, unrelated spans and lookalike operations, inherited child sampling, repeated registration, and
exactly-once disposal. Direct-source registration remains a unit-test comparison. SDK integration tests validate the
extension through public submission APIs without Docker.
Package checks verify that core has no OpenTelemetry dependency, the integration package has minimum dependency ranges,
and independently selected package versions produce the correct core dependency. README examples recommend installing the integration package and
registering its extension, and link its NuGet page in the tracing and metrics sections. They also explain direct AddSource registration: the application's sampler
then decides Handle roots independently, so a sampled Execute may lack its corresponding recorded Handle trace.
Metrics-only users can register AddMeter("RequestBatcher") without the integration package; that guidance belongs in
the metrics section. The sampling diagram shows the registered integration policy and names the source and destination of
each link, without depending on a sample's code or a specific application sampler.

`MaxConcurrency` bounds active handler batches only. Execute Activities begin at submission and can remain alive during
admission, queue residence, and completion. Their count is therefore not bounded by the handler slot count, and neither
Activity nor per-submission shared tracing state is reused by execution slot. The diagram and its Chinese counterpart
are isomorphic; each SVG has a matching PNG backup. README guidance must distinguish policy propagation from correlation
and must not describe 0% sampling as equivalent to the no-listener path.

### Span names and outcomes

Span names use stable base names. For a named registration, the public name follows the operation,
separated by a space, such as `RequestBatcher.Execute pricing-queries` and
`RequestBatcher.Handle pricing-queries`; both spans also include
`requestbatcher.name`. Unnamed registrations keep the base name and omit the tag. The name is
the exact public DI registration name, never an internal topic or pipeline key. It is available at Activity creation for
sampling and carried once on the coordinator through dispatch and response/delegate adapters,
without adding per-request state. Tags also use `requestbatcher.request.type`,
`requestbatcher.batch.size`, and `requestbatcher.canceled`. Both Execute and Handle use the public request type's
simple name for `requestbatcher.request.type`, matching metrics and unwrapping internal response entries. Diagnostics
owns the shared type-name resolver; callers cache its result rather than reflecting per request. Existing span queries
and samplers filtering fully qualified request types must use short names. Tests cover class and delegate handlers,
with and without responses, and creation-time tags. Faults set Error and
`error.type`; payloads, tenant keys, exception messages and stacks are not emitted.
Expected task cancellation sets the cancellation tag without Error; a faulted
task containing OperationCanceledException remains a fault. Tracing preserves
original exception references, aggregate failures, cancellation tokens, and task
terminal state. The caller's current Activity is restored before ExecuteAsync
returns its task. Handler failures retain the existing fan-out semantics.

All distinct contexts are supplied as links; SDK/exporter link limits may drop
links for large batches. Applications needing complete large-batch correlation
must configure their provider's link limit. Tracing is best effort when sampling
or backend retention drops either endpoint. Core does not force recording; the optional
integration extension changes the SDK's batch decision when it is registered.

## Internal flow and performance

Diagnostics owns a shared ActivitySource and tracing helpers. Public entry points
check HasListeners before creating closures or tracing state. With no listener,
the existing task path is used without an async wrapper, span, link collection,
or extra per-item tracing allocation. Traced pending requests use an internal
subclass that stores shared submission tracing state, keeping the untraced pending object's size unchanged.
Propagation-only state contains the submission context. A separate recorded-state subtype stores the first batch
context directly and allocates a collection only for additional batches, protected against concurrent dispatch.
Completing the state drains those contexts exactly once and ignores later additions. The recorded-only fields do not
increase propagation-state size. Neither state holds an Activity or participates in a pool.
Fixed call names and immutable sampling tags are prepared once per coordinator/adapter rather than per submission.
They remain available to samplers at Activity creation and are not mutable through the shared tag collection.
The metadata type, sampling-dependent state, deduplication, and Activity lifetime stay under Diagnostics. Submission
adapters only hold metadata and invoke the tracing boundary; queue admission, capacity, dispatch, and request
completion do not inspect sampling flags or manage Activities. Completion sources support an internal publication
notification; Diagnostics supplies observed completion subclasses only when a call Activity exists. Ordinary completion
sources retain their existing fields. The notification separates preparing an outcome from publishing the caller's Task.
The no-listener hot path does not access them. An unsampled submission, or one that does not request all Activity data,
allocates neither synchronization state nor a collection for links from Execute to Handle. Batch dispatch supplies
every distinct participating context to the Handle sampler, including unsampled contexts, only when at least one
submission context is sampled. Otherwise it skips the link collector entirely. It adds the batch context only to
submissions collecting links from Execute to Handle. A batch link collection stores its first submission directly
and lazily creates one dictionary for additional distinct TraceId/SpanId pairs. It exposes links from Handle to
Execute by enumeration and retains the same submission references to add links from Execute to Handle.
This avoids growing separate link and recipient
lists; repeated items from one explicit submission allocate no dictionary. Repeated sampler enumeration is supported
and does not mutate the collection. Dictionary capacity grows with observed distinct contexts, rather than reserving
space for every item in a duplicate-heavy batch.
Collecting links from Execute to Handle requires both IsAllDataRequested and Recorded: record-only and
propagation-only Execute Activities do not collect those links.
Call completion writes the collected links before stopping the Activity, so exporters see
the complete links without concurrent Activity mutation. Dropped calls retain only their
available context and do not collect links to Handle. Traced pending items carry correlation contexts and an observed
completion source; request payloads, routing, cancellation, and public lifecycle semantics stay unchanged.

An enabled call starts its Activity, invokes the core path, and restores caller ambient context synchronously.
An observed completion registers before admission. It holds publication until the submission boundary identifies
the returned Task. When that Task is the registered completion's Task, Diagnostics ends the span and publishes the
existing completion directly, eliminating the extra tracing TCS and async waiter. A lock on the observer coordinates
completion racing with submission; callbacks run outside the lock and publication occurs exactly once. The observer
captures ExecutionContext while the call Activity is current and restores it for span completion without leaking the
Activity or AsyncLocal values into consumer/cancellation threads.
Already completed unobserved tasks are returned unchanged after ending the span. Producer slow paths and response-group
materialization can return a different Task; their observed source is released without ending the span, and a Diagnostics
completion bridge covers the entire returned Task. This preserves admission cleanup, response conversion, all original
faults, and cancellation semantics while optimizing the ordinary admission path. Explicit request-only groups observe
only the final aggregate publication, never an individual item's completion. No Task.Run or extra thread is added per call/batch.
Response and delegate adapters invoke uninstrumented core methods under their
outer call span to avoid duplicate spans and include their own preparation.

## Validation and compatibility

Public batching APIs and handler lifetimes remain unchanged. Tests use real DI,
ActivityListener, and the OpenTelemetry SDK without external services. Cover
multiple callers in one batch, a call split across batches, parent/context
restoration, downstream children, scope disposal and response materialization,
Handle-to-Execute and Execute-to-Handle links visible to SDK processors at call end, concurrent batches for one call,
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
Allocation regressions are evaluated against the published 0.3.2 package with the same 20 ms workload and SDK
sampling rates (disabled, 0%, 1%, 10%, 100%). Verify mixed sampled/dropped submissions in one batch, links from
unsampled submissions in mixed batches, the no-links path for all-unsampled batches, creation-time tag availability, and
deduplication of links from Execute to Handle. Activity lifetimes, original exception collections, and cancellation
tokens are unchanged by allocation reductions.
Completion tests cover outcome preparation before and after observer attachment, direct Task identity, fallback wrapper
identity, captured AsyncLocal/Activity restoration, aggregate faults, and exactly-once span completion before publication.
Focused tests cover zero/one/multiple links from Execute to Handle, concurrent additions, idempotent completion, sparse repeated
contexts, multiple enumeration of sampler links, and both propagation-only and record-only states. Compare isolated
source snapshots before/after the reduction, including disabled tracing, low sampling, full sampling, and explicit groups.
Tracing and metrics registration extensions ship in the optional RequestBatcher.OpenTelemetry package.

## Live integration coverage

The integration project also contains bounded in-process stress tests using the public API and real OpenTelemetry SDK.
These run without Docker and cover repeated concurrent single/group submissions at 0% and 100% sampling, constrained
capacity, and many handler batches. Each request ID must be processed exactly once; recorded Execute/Handle
links must match actual batch membership in both directions, with no parent links or contexts from an earlier wave.
A separate coordinated stress case holds handler dispatch while queued work is canceled, then drains success/failure
outcomes during disposal and rejects later submissions. Completion and failure checks use barriers and captured
outcomes rather than timing-only assertions. Tests have bounded deadlines, dispose providers, and keep telemetry
capture bounded to the current wave. The existing CI invocation runs these tests without extra environment steps.

The opt-in `tests/it/RequestBatcher.Observability.IntegrationTests` project targets .NET 10 and owns an isolated
Testcontainers environment containing the existing sample's PostgreSQL, OTEL-LGTM, Cart, and Pricing services.
The fixture builds the application images, assigns random host ports, and explicitly cleans up partial or completed
startup; Testcontainers' resource reaper provides an additional cleanup guard. Ordinary unit tests remain independent
of Docker and network access. The live suite uses real Cart HTTP requests, Grafana's Tempo and Prometheus data-source
proxies, and unique W3C trace IDs to isolate its evidence from other sample traffic. The Compose sample remains
available for manual inspection but is not started by the automated integration project. No production dependency or
public API changes are required.

Concurrent Cart requests each submit 2–10 product IDs through the native enumerable API. The suite verifies ordered
prices including duplicates, exactly one Execute span per HTTP request, its HTTP parent, and independent Handle
roots linked to the corresponding Execute contexts. Each Execute must link back to exactly those batch contexts;
both directions must be unique and symmetric, with no unrelated batch links. Every submitted caller
must be represented, and a concurrent burst must demonstrate a batch containing multiple callers. Batch size counts
product items and stays within 1–20; the test does not require full batches or one handler invocation per submission.
Each related batch must contain the Cart HTTP client → Pricing HTTP server → Npgsql query parent chain, without a
Pricing batcher or error spans. Repeated fresh bursts also detect accidental reuse of an earlier request's context.

Telemetry assertions poll for complete evidence with a bounded deadline, accounting for asynchronous export and
indexing. Failures identify missing trace IDs, links, or metrics rather than passing on partial data. Prometheus checks
verify all five RequestBatcher instruments for the configured name and positive post-request increments for submitted,
processed, and batch counters. Histogram counts and sums must also advance. Integration runs are explicitly enabled;
without the opt-in environment variable, tests are reported as skipped.
