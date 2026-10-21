# Cart and pricing observability sample

## Contract and compatibility

`samples/RequestBatcher.Observability` is a local Docker Compose demonstration of two independent .NET 8 Web APIs.
`Cart/RequestBatcher.Observability.Cart.csproj` accepts product quantities and calculates price quotes.
`Pricing/RequestBatcher.Observability.Pricing.csproj` exposes price lookup endpoints and uses the named
`product-reads` RequestBatcher pipeline to batch PostgreSQL reads. Cart references HTTP and OpenTelemetry packages
only; Pricing references the local RequestBatcher OpenTelemetry adapter and Npgsql. Neither project references the
other. HTTP DTOs are small local contracts. No orders or payments are created, and normal operation is read-only.
The library API and SDK policy remain unchanged.

Compose starts Cart, Pricing, a traffic generator, PostgreSQL, and Grafana OTel LGTM. Separate Dockerfile targets build separate images.
LGTM follows the supplied RocketMQ reference (`grafana/otel-lgtm:0.29.0`, `/tmp/ready`, persistent `/data`, datasource
and dashboard provisioning). Host ports bind to loopback and are configurable. Existing volumes are preserved.

## Internal flow

Cart exposes `POST /cart/quote`, accepting an array of `{ productId, quantity }` entries. It validates 1–64 entries,
positive IDs, and quantities from 1 to 100, fetches products through Pricing's `GET /prices?ids=...` batch API, and
returns quote lines with product ID, name, unit price, quantity, and line total plus the total price. Missing products
return 404; invalid carts return 400. Pricing failures produce a failed HTTP response, never a partial successful quote.
Pricing also exposes `GET /prices/{id}` for inspecting a single lookup. Both services expose untraced `/health` routes.
A separate `CheckHealth.cs` file-based application probes these routes for Docker; it checks HTTP availability, not
database readiness. Business routes live in `CartEndpoints.cs` and `PricingEndpoints.cs`; each `Program.cs` only
composes and starts its host.

A separate `traffic-generator` container simulates external clients using the `GenerateTraffic.cs` single-file C# application.
It starts after Cart is healthy, uses eight bounded asynchronous HTTP loops, alternates requests of
1 and 32 entries, validates response IDs, quantities, and totals, and waits 250 ms between requests. Failures are logged
and retried at the same bounded rate. Stopping this container leaves both APIs available for manual requests. Neither
Web API owns a synthetic workload. Shutdown stops the generator; accepted Pricing requests follow RequestBatcher
completion semantics.

Cart's ASP.NET Core request span is the root for each simulated user request. HttpClient propagates context from Cart
to Pricing's ASP.NET Core request span. Pricing submits to `product-reads` (batch size 16, maximum concurrency 1,
bounded pending capacity). Its handler deduplicates IDs within each batch and runs one parameterized PostgreSQL SELECT,
assigning a response to every entry. Missing product responses are explicit nulls. The database initialization script
seeds IDs 1–100 once. The generator logs request duration and outcomes but does not export its own spans.

`RequestBatcher.Execute` stays in the HTTP trace and measures caller-visible submission duration. Independent
`RequestBatcher.ProcessBatch` Consumer roots link to participating Execute spans and own Npgsql query child spans.
Explicit submissions can split across batches; concurrent calls can share batches. Both spans carry
`requestbatcher.name=product-reads`. Only context identifiers cross the queue; ambient request state is isolated.

OTLP/HTTP exports traces, framework HTTP metrics, and logs. Service names are `requestbatcher-cart` and
`requestbatcher-pricing`. Always-on sampling and a 256-link limit keep the demonstration visible; these are not
production sampling recommendations. Grafana provisions Tempo, Prometheus, and Loki and searches both RequestBatcher
span types. TraceQL link lookup connects a caller trace to its batch traces without implying a single trace tree.
RequestBatcher provides spans and existing logs, not a new library metrics contract. It is in-process and cannot recover
pending requests after process failure. Explicit teardown commands distinguish preserving and deleting volumes.

## Validation

Build and format both projects and run the full existing library test project. Validate Compose and provisioning.
Run the stack and verify healthy services, valid single/grouped products, quote totals, invalid query rejection, and
missing-product handling. Observe successful continuous quotes. Query Tempo to prove Cart-to-Pricing propagation,
Execute-to-batch links, and Npgsql children of batch spans. Check HTTP metrics and log ingestion. Leave the environment
running for review; do not merge or publish yet. Run performance benchmarks separately from the sample workload.
