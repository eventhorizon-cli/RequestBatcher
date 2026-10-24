# Cart and pricing observability sample

## Contract and compatibility

`samples/RequestBatcher.Observability` is a local Docker Compose demonstration of two independent .NET 10 Web APIs.
`Cart/RequestBatcher.Observability.Cart.csproj` accepts small carts with product quantities and calculates price quotes.
`Pricing/RequestBatcher.Observability.Pricing.csproj` exposes price lookup endpoints and reads PostgreSQL directly.
Cart references RequestBatcher, HTTP, and OpenTelemetry packages; Pricing references Npgsql and its official
OpenTelemetry integration. Neither project references the
other. HTTP DTOs are small local contracts. No orders or payments are created, and normal operation is read-only.
The library API and SDK policy remain unchanged.

Compose starts Cart, Pricing, a traffic generator, PostgreSQL, and Grafana OTel LGTM. Separate Dockerfile targets build separate images.
LGTM follows the supplied RocketMQ reference (`grafana/otel-lgtm:0.29.0`, `/tmp/ready`, persistent `/data`, datasource
and dashboard provisioning). Host ports bind to loopback and are configurable. Existing volumes are preserved.

## Internal flow

Cart exposes `POST /cart/quote`, accepting an array of 2–10 `{ productId, quantity }` entries. It validates the item count,
positive IDs, and quantities from 1 to 100, then submits the complete ID sequence once through the native
`IRequestBatcher<int, Product?>` enumerable overload. The batch handler receives up to 20 product requests, which can be
mixed across multiple Cart HTTP requests, deduplicates their IDs, fetches products through Pricing's `GET /prices?ids=...`
batch API, and maps each response back in its original request order. `BatchSize=20` counts product requests, not Cart
submissions; a single 2–10 item Cart submission may be split across multiple handler batches. It returns quote lines with
product ID, name, unit price, quantity, and line total plus the total price. Missing products
return 404; invalid carts return 400. Pricing failures produce a failed HTTP response, never a partial successful quote.
Pricing also exposes `GET /prices/{id}` and `GET /prices?ids=...` for inspecting lookups; the grouped endpoint accepts the
up to 20 IDs sent by one batch handler. Both services expose untraced `/health` routes.
A separate `CheckHealth.cs` file-based application probes these routes for Docker; it checks HTTP availability, not
database readiness. Business routes live in `CartEndpoints.cs` and `PricingEndpoints.cs`; each `Program.cs` only
composes and starts its host.

A separate `traffic-generator` container simulates external clients using the
`RequestBatcher.Observability.TrafficGenerator` console project.
It starts after Cart is healthy, uses 32 bounded asynchronous HTTP loops, sends a random 2–10 item cart on each iteration,
validates response IDs, quantities, and totals, and waits 250 ms between requests. Failures are logged and retried at the
same bounded rate. Neither Web API owns a synthetic workload. Accepted Cart batcher requests follow RequestBatcher
completion semantics.

Cart's ASP.NET Core request span is the root for each simulated user request. Each request enters the
`pricing-queries` RequestBatcher once with its complete ID sequence (batch size 20 product requests, maximum concurrency 2,
bounded pending capacity). Its static delegate handler combines and deduplicates IDs across requests, makes one HTTP
request to Pricing, then maps each Pricing result back to every original request. Pricing's ASP.NET Core handler
runs one parameterized PostgreSQL SELECT. Missing product responses are explicit nulls. The database initialization script
seeds IDs 1–100 once. The generator logs request duration and outcomes but does not export its own spans.

`RequestBatcher.Execute pricing-queries` stays in the Cart HTTP trace and measures caller-visible submission duration.
Independent `RequestBatcher.Handle pricing-queries` Consumer roots link to the distinct Cart `Execute` spans that
contributed product requests and contain the Cart-to-Pricing HTTP child span. One Cart `Execute` span may link to multiple
batch roots when its 2–10 product requests are split, while a batch can mix requests from concurrent caller traces. Npgsql
creates the database span under Pricing's HTTP span. Both
RequestBatcher spans carry `requestbatcher.name=pricing-queries`. Links work in both directions; Execute collects batch
contexts during dispatch and adds its links before ending. Only context identifiers and shared link-collection state
cross the queue; ambient request state is isolated.

OTLP/HTTP exports traces, framework HTTP metrics, RequestBatcher metrics, Npgsql metrics, and logs. Service names are
`cart` and `pricing`, set through `OTEL_SERVICE_NAME` with matching application defaults. Grafana queries and integration
assertions use those service identities. Existing stored telemetry retains its original service names.
Dashboard trace panels appear first, distinguish caller waiting from batch execution, and explain navigation through
span links. Metrics and logs follow them.
Grafana's span details label these navigable entries `References` and include both
parent references and span links. An Execute's HTTP parent shares its trace ID;
its handler links point to independent Handle traces. Panel descriptions and
sample instructions explain this distinction.
Always-on sampling and a 256-link limit keep the demonstration
visible; these are not production sampling recommendations. Grafana provisions Tempo, Prometheus, and Loki and
searches both RequestBatcher span types. TraceQL link lookup connects a caller trace to its batch traces without
implying a single trace tree. RequestBatcher metrics cover accepted and processed product requests, processed batches,
batch size in product requests, and batch duration. Their request-type dimension uses `Int32`, the public request type's
short name. Instrument names contain one `requestbatcher` prefix. It is in-process and
cannot recover pending requests after process failure.

## Validation

Build and format both projects and run the full existing library test project. Validate Compose and provisioning.
Run the stack and verify healthy services, valid 2–10 item carts, quote totals, invalid cart rejection, and
missing-product handling. Observe successful continuous quotes. Query Tempo to prove Cart-to-Pricing propagation,
Execute-to-batch links, and Npgsql children of batch spans. Check HTTP metrics and log ingestion. Keep validation bounded
and stop the environment immediately after collecting evidence, including on failure. Run performance benchmarks separately
from the sample workload.
