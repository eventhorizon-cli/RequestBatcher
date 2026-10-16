# Named Batcher Registration

## Contract

`AddRequestBatcher<TRequest, THandler>(batcherName, handlerLifetime, configure)`
and `AddRequestBatcher<TRequest, TResponse, THandler>(batcherName,
handlerLifetime, configure)` register independent singleton batchers in the
application container. Callers resolve them with `GetRequiredKeyedService` or
inject them with `[FromKeyedServices]` using the existing `IRequestBatcher`
interfaces. Handler lifetimes remain explicit.

A logical pipeline is identified by its original request type and name.
Equal names for the same request type are rejected, including registrations that
mix request-only and response-bearing handlers or different response types.
Different names and the unkeyed pipeline may coexist. Names use ordinal,
case-sensitive equality. Null, empty, and whitespace-only names are rejected;
use the existing overload for the unnamed default.
Duplicate registration is rejected before invoking configuration or changing
service descriptors. Invalid options and lifetimes likewise leave registrations
unchanged.

Each pipeline has its own handler, options snapshot, queue capacity, partition
routing, dispatch concurrency, pending state, and disposal operation. There is
no broadcast or forwarding between names. A name selects a whole pipeline;
`UsePartitionKey` routes individual requests within that pipeline. Equal
partition keys still provide no handler ordering or serialization guarantee.

## Internal Flow

All pipelines use the application's shared `IBufferQueue`. Each named pipeline
registers an internally generated unique topic name. The coordinator receives
it for both producer and consumer creation. Topics and queue APIs remain internal.

The application handler is registered as a keyed service with the supplied name
and lifetime. Singleton handlers are resolved during coordinator composition;
scoped and transient handlers are resolved once in an async scope owned by each
handler batch. Response pipelines project original request options to response
items and use a private key for their adapter and coordinator so those internal
registrations cannot collide with user pipeline keys.

Each original request options snapshot is available through named options:
`IOptionsMonitor<RequestBatchOptions<TRequest>>.Get(batcherName)` (and scoped
`IOptionsSnapshot<RequestBatchOptions<TRequest>>.Get(batcherName)`). Named registrations
do not configure the unnamed default options. Configuration runs once during
registration. Topology and coordinator configuration use the same validated
snapshot. No nested service
provider or production dependency is introduced.

## Compatibility and Lifecycle

The named overloads use .NET keyed service registration internally.
The API is additive and supports both declared target frameworks. Existing
unkeyed registration, resolution, and options retain their contract. Disposing
one coordinator stops its admission and drains its accepted requests without
stopping other pipelines. The application container owns all pipelines and
shared queue resources and must be disposed asynchronously.

Completion, explicit batch submission, pre-dispatch cancellation, exception
fan-out, and response ordering reuse the existing coordinator. Once dispatched,
requests observe the actual handler outcome. RequestBatcher is in-process and
does not recover pending requests after process failure.

## Primitive Request Example

The PostgreSQL sample registers its price-query handler under `batcherName: "price"`
with `int` requests and `decimal?` responses. The handler deduplicates primitive
IDs, widens them to `long` for PostgreSQL bigint lookup, and maps each original
ID to its price or null.
`GET /prices/{productId}` submits one ID and `POST /prices/query` submits a JSON
array of IDs, returning responses in input order including duplicates and
missing products. Both inject the same named batcher with `[FromKeyedServices]`.
The price-update pipeline retains its structured payload. Query endpoints accept
positive 32-bit IDs and return price values rather than complete update records.
The root and package READMEs show the same primitive request/response contract.
Sample validation compiles the endpoints and exercises the actual query handler
with duplicate and missing IDs against PostgreSQL when Docker is available.

## Validation

Deterministic unit tests cover same-type named and unnamed coexistence, handler
and options isolation, duplicate and invalid registration without mutation,
scoped/transient handler resolution and
async disposal, response batches and input ordering, partition projection,
backpressure isolation, failure isolation, and independent coordinator shutdown.
Run the focused registration tests, the complete test project on both target
frameworks, formatting, a Release solution build, and package creation.
