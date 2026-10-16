# Delegate Handler Extensions

## Response Entry Naming

The public response-bearing wrapper is `RequestBatchEntry<TRequest, TResponse>`.
All handler signatures, response assignment extensions, examples and tests use this
name and `entry` / `entries` for local values. Renaming the public wrapper requires
consumer source updates; its response assignment, validation and completion
semantics remain unchanged.

## Contract

The existing `IRequestBatcher<TRequest>` and `IRequestBatcher<TRequest, TResponse>`
submission interfaces and typed-handler registration/execution behavior remain unchanged.
Response handler signatures use the renamed `RequestBatchEntry` type, which is a
source and binary breaking change requiring consumers to update and rebuild. Inline
handlers are an opt-in registration mode provided by `AddDelegateRequestBatcher`
extension overloads without a handler type or lifetime:

```csharp
services.AddDelegateRequestBatcher<int>();
services.AddDelegateRequestBatcher<int, decimal?>(batcherName: "price");
```

Both request-only and response registrations support default and named pipelines,
the same options, and the existing uniqueness rule for original request type plus
name. A named inline registration exposes the same named options snapshot. Invalid
or duplicate registration fails before changing services or running configuration.

`RequestBatcherDelegateExtensions.ExecuteAsync` adds single-request and explicit-list
overloads that accept `Delegate handler` before the optional caller cancellation
token. Response handlers receive `IReadOnlyList<RequestBatchEntry<TRequest,
TResponse>>`; request-only handlers receive `IReadOnlyList<TRequest>`. These are
extension methods, with no new required members for existing interface implementers.
Handlers must use static implementations: lambdas, anonymous methods and local
functions explicitly declare `static`, and method groups refer to static methods.
Delegates may also be supplied through variables, fields, parameters, properties
and factories. Callers remain responsible for the static-target contract when
compile-time analysis cannot establish the target.
Calling an inline overload on a typed-handler or third-party implementation fails
before admission. Calling the original overload on an inline registration likewise
fails before admission; every inline submission explicitly supplies its handler.

Each inline pipeline binds one delegate on its first valid submission. Repeated
executions with an equal delegate reuse that binding; an unequal delegate is an
attempt to register a second handler and fails before admission. Equality uses a
reference fast path followed by `Delegate.Equals`, not merely method identity.
Binding is atomic under concurrent first calls. Invalid signatures do not establish
a binding. Canceled calls and null explicit lists do not establish a binding.
Different lambda call sites should use different names when they represent
different handlers. The delegate is retained for the singleton pipeline lifetime.

## Invocation and Dependency Injection

One singleton `DelegateRequestBatchHandler<TRequest>` serves each delegate pipeline. It passes
each actual batch to a cached typed invoker; delegates do not accompany individual
queued requests, and batches are never split or regrouped by delegate identity.
The existing coordinator, pending-request state, partition routing, backpressure,
batch concurrency and completion machinery remain responsible for execution.
Explicit delegate copies and delegate `Invoke` wrappers retain their actual outer
delegate type for invocation and equality. Binding follows delegate targets through
these wrappers to read the original method's injection, default-value and async
metadata. Every wrapped invocation list must still contain exactly one method.
This normalization runs only during initial binding, not on warm submissions or batches.

The first parameter must be the exact batch list type. Remaining parameters may
contain one `CancellationToken`, supplied from the batch lifecycle, and ordinary
DI services. The entire token parameter may be omitted; when declared, it must
not have a default value such as `= default`. The optional cancellation argument
on `ExecuteAsync` belongs to the caller and is distinct from this batch token.
`[FromKeyedServices(key)]` selects a keyed service and requires an
explicit key; inherited-key lookup is rejected because the forwarding handler
has no application service key. Other parameters are required services by default,
so ASP.NET applications may annotate
them with `[FromServices]` without introducing an ASP.NET dependency in the core
library. Injected `IServiceProvider` refers to the batch's provider. Optional
parameters, by-reference parameters, open generic and multicast delegates are
rejected rather than given ambiguous binding rules.

Supported returns are synchronous `void`, `Task` and `ValueTask`; `async void` and
result-bearing returns are rejected. Response handlers assign every response via
the existing entry APIs, including missing-product results. Missing responses fail
the batch as they do with a typed handler.

When services are requested, each dispatched batch creates and asynchronously
disposes its own DI scope. Services are resolved once per handler invocation, not
once per request, and never captured from the calling HTTP request scope. Delegates
without service parameters need no DI scope. Completion includes invocation,
response validation and scope disposal. Exceptions, cancellation after dispatch,
draining and capacity release reuse the existing behavior. RequestBatcher remains
in-process and does not recover accepted work after process failure.

The delegate forwarding handler and its bound delegate are always singleton per
pipeline, so delegate registration has no `ServiceLifetime` argument. Injected
services retain their own DI lifetimes: singleton services are shared by the
container, scoped services are shared within one batch scope, and transient
services are resolved separately for each parameter. Only the invoker and its
parameter metadata are cached; resolved service instances are never cached by the
forwarding handler. A cancellation-token parameter alone does not require a scope.
Typed-handler registrations continue to honor their explicit handler lifetime,
including handlers without constructor dependencies.

## Static Handler Analyzer

The main NuGet package includes a C# analyzer under `analyzers/dotnet/cs`.
Diagnostic `RB0001`, an error by default, requires statically known handler targets
to be static. Supported forms include static lambdas, static anonymous methods,
static local functions, ordinary and closed generic static method groups, explicit
delegate construction, and delegates stored in variables or fields. Parentheses,
casts and delegate conversions do not restrict the supported forms. Non-static
lambdas, instance methods, non-static local functions and bound extension-method
receivers are rejected when their target is known. Unrelated APIs are unaffected.
Delegate parameters, properties, factory results and mutable storage are accepted
when their target cannot be proven; absence of proof is not an error. A known
constructed delegate type still permits checking its `Invoke` signature. Simple
unchanged local initializers may be followed to recover the target and actual
delegate type. Field and property values are not assumed to equal their initializers.
For opaque sources, reference-type generic variance can make the runtime delegate
type differ from its declared type. Signature checks defer errors affected by that
variance rather than rejecting a possibly valid runtime delegate. Fixed signature
parts, including value-type arguments, remain checked.
The analyzer does not attempt whole-program value-flow analysis or execute user code.

Diagnostic `RB0002`, also an error by default, validates each statically known
handler signature at compile time. Its first parameter must be exactly
`IReadOnlyList<TRequest>` for request-only submissions or
`IReadOnlyList<RequestBatchEntry<TRequest, TResponse>>` for response submissions,
using the request and response types inferred for the selected extension overload.
Nullable reference annotations do not change this runtime type identity, and
generic callers with matching type parameters remain supported.

Return types must be `void`, non-generic `Task` or non-generic `ValueTask`;
`async void` and by-reference returns are rejected. Optional, by-reference,
pointer, function-pointer and ref-like parameters are rejected. After the batch
parameter, at most one unkeyed `CancellationToken` is allowed; other parameters
are DI services. `[FromKeyedServices]` requires an explicit key (including explicit
`null`) and cannot annotate the batch or cancellation-token parameter. Ordinary
service parameters and optional `[FromServices]` annotations remain valid.

Signature validation uses semantic symbols for the target method and its constructed
delegate's `Invoke` method, so aliases, generic methods and explicit delegate casts
are handled according to their actual types. When a target is known, its parameter
attributes supply DI binding metadata, including keyed-service selection. `RB0001`
takes precedence for known non-static targets; `RB0002` reports the first signature
issue with a specific reason. The analyzer does not infer DI registrations, check service
availability or enforce delegate identity across separate submissions. Incomplete
or erroneous compiler symbols must not cause analyzer failures. Runtime binding
validation remains authoritative when analyzers are disabled, bypassed or unable
to establish a target. Registration, admission, scheduling and service lifetimes
remain governed by the existing pipeline contracts.

The analyzer is a source-level development constraint and can be disabled by the
consumer. For opaque delegate sources, runtime binding validates the actual signature,
service metadata and single binding; it does not prove source-level staticness.
The runtime does not try to infer a static lambda from `Delegate.Target`
or `MethodInfo.IsStatic`, which are not reliable indicators of its source syntax.
The analyzer targets .NET Standard 2.0 and Roslyn 4.8 to support C# 12 compiler hosts.
Roslyn dependencies are private build/test dependencies, not application runtime
dependencies. No standalone analyzer package is published.

## Performance and Compatibility

Only initial binding reflects over the delegate's signature and parameter
attributes and compiles an expression-based typed invoker. The invoker uses direct
delegate invocation and typed service conversions. Warm submission performs a
binding check and existing admission; warm batch execution performs no reflection,
`DynamicInvoke` or argument-array construction. Initial compilation cost is separate
from steady-state benchmarks. Automatic binding requires dynamic code and retained
reflection metadata; NativeAOT and trimming support are not promised for this mode.
Existing typed-handler registration remains available without this requirement.

Delegate APIs are additive extensions; the response wrapper rename is a separate
source and binary breaking change. Internal wrapper batchers expose inline
submission through internal capability interfaces; existing interface implementers
and typed-handler scheduling and admission paths are unchanged. Provider-owned coordinators retain
idempotent async disposal. Existing registration methods still detect duplicate
inline registrations through the same registration markers.

## Validation

Runtime tests cover default/named and request/response modes, list result ordering,
same-handler reuse, different-handler rejection before admission, concurrent first
binding, failed signature recovery, ordinary and keyed service resolution, per-batch
async scope disposal, exception fan-out, missing responses, caller cancellation,
backpressure, partition projection and provider draining. Existing typed-handler
tests remain regression coverage for unchanged execution paths.
Lifetime tests also verify scope elision without services, singleton forwarding
handler registrations, and singleton/scoped/transient parameter identity and
disposal across batches.

The standalone `RequestBatcher.Delegates` PostgreSQL sample registers a default
request-only price-update delegate pipeline and a named `price` response pipeline.
Its `POST /prices` and `POST /prices/query` endpoints each supply one static lambda
directly to `ExecuteAsync`. Both lambdas inject a keyed scoped `PriceStore` from
their batch scope; the data source is singleton. The write delegate keeps the
highest version per product within its batch, with a conditional database upsert
rejecting stale writes across batches. The query delegate deduplicates IDs in its
batch and assigns nullable responses in original request order. Registration has
no handler lifetime argument. Comments and usage documentation distinguish the
singleton delegate from per-batch services and preserve the durability, partition
routing and duplicate-merging boundaries. The original typed-handler sample
retains its implementation. Sample builds check the actual lambda syntax;
database smoke checks require PostgreSQL.

Analyzer tests compile representative call sites and inspect diagnostics: static
and non-static lambdas, captured state, named arguments, both submission forms,
explicit casts, delegate variables and fields, method groups, static local functions,
static anonymous methods, generic static methods, unrelated APIs and existing
handler APIs. Runtime tests execute these forms with ordinary and keyed DI parameters,
verify equal delegates from separate conversions reuse a binding, and verify a
different method still fails before admission. Delegate copies retain keyed service
metadata and cannot hide multicast handlers, optional source parameters or async void.
A packaged consumer build verifies analyzer delivery, with a static
example compiling and a non-static example failing with `RB0001`.
Signature diagnostics additionally cover all four submission overloads, exact
batch element types, supported and unsupported return types, async void, invalid
parameter modifiers and defaults, cancellation-token multiplicity, keyed-service
attributes, generic callers, aliases and casts. A packaged consumer verifies
`RB0002` delivery without introducing analyzer dependencies at runtime.

Steady-state in-memory benchmarks compare typed handlers and inline delegates with
identical requests, responses, configuration, batching gates and correctness checks,
both without injected services and with the same scoped dependency. Record time
and allocations, distinguish initial binding from measurement, and report measured
differences without promising zero overhead or inferring downstream throughput.

The performance matrix varies batch size (1, 32, 256) and handler concurrency
(1, 8) independently for response workloads. Request-only benchmarks compare
singleton, scoped and transient service injection, including explicit keyed
services, with identical service lifetimes and work on both paths. Fresh-pipeline
benchmarks measure the first submission separately: provider construction and
batcher resolution happen before timing, while delegate binding, expression
compilation, first dispatch and response completion are included. Fresh-pipeline
results are not process startup measurements. All cases validate completed work;
short validation jobs establish executability rather than performance conclusions.
Synchronous-completion response benchmarks compare `void`, already-completed
`Task` and `ValueTask` delegates against a typed handler with identical work, so
return adaptation is covered separately from the asynchronous wave-gate workload.
