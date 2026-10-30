# Dependency injection composition

## Contract

`AddRequestBatcher` and `AddDelegateRequestBatcher` retain their public overloads, handler lifetimes, named-service
selection, and application-owned container. Coordinators and adapters receive explicit constructor dependencies;
they do not hold an `IServiceProvider` or expose dependencies through forwarding properties. Metrics remains one
container-owned singleton, separate from tracing. No nested provider is created.

## Internal composition

Each batcher registers immutable composition metadata: its topic name, public batcher name, and (for keyed batchers)
validated options snapshot. Default coordinators continue to obtain their options through `IOptions<T>`.
The configuration creates one immutable `RequestBatcherMetadata` shared by the coordinator, adapters and consumer.
It holds one short request type name shared by logs, metrics and traces, the batcher name, and precomputed trace name/tags. This is data
only: Metrics and Tracing keep separate recording behavior, tag constants and resource ownership.
`RequestBatchHandler<T>` is a registered singleton execution delegate. A singleton handler contributes its bound
`HandleAsync` method; a scoped or transient handler uses a constructor-injected scope factory and resolves a handler
inside an async scope for each batch. That wrapper holds no handler instance between batches.

The internal coordinator receives the queue, logger, options, Metrics, execution delegate, and batcher configuration
through its public constructor. DI constructs it directly from a type registration; no coordinator factory is needed.

Batcher-local dependencies inherit the key of the service being constructed using the existing DI 10 dependency's
`FromKeyedServices` support. Unkeyed registrations inherit a null key and use ordinary services. Internal batcher keys
remain separate from public names. Adapter types are registered directly; small alias callbacks map public interfaces
to the same batcher instances. Response-handler aliases can cross from an internal batcher key to its public handler
key, preserving handler lifetime and service identity. These aliases do not construct duplicate coordinators.

Delegate handlers receive their optional response-validation callback through the same batcher key. Their parameter
injection still resolves services within a batch scope; service-free delegates create no scope.

Internal handler implementations and the execution delegate live under `Handlers`. Delegate binding and its submission
adapters remain grouped in `Handlers/Delegate`. Namespaces follow the directories; public handler contracts and
registration extensions retain their root namespace. This organization adds no runtime abstraction or service.

## Compatibility and validation

The coordinator becomes an internal implementation type. Consumers must use `IRequestBatcher<TRequest>` rather than
reference `RequestBatchCoordinator<TRequest>` directly; code using that concrete type must migrate. The implementation
uses the already-required DI 10 abstractions on both target frameworks. Direct internal tests construct batcher
configuration explicitly. Validate default/named and typed/delegate request-only/response batchers,
coordinator/interface identity, option snapshots, all three handler lifetimes, and asynchronous scope disposal. Run the
full unit suite on net8.0 and net10.0, SDK observability tests, and relevant correctness/overhead workloads. No change
to metric names, histogram advice, trace boundaries, admission, or shutdown outcomes is intended.
