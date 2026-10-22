# RequestBatcher cart and pricing observability sample

**English** | [简体中文](README.zh-CN.md)

This Docker Compose sample models a small read-only pricing flow. A separate .NET 10 traffic-generator console app
acts as an external client and calls the Cart Web API. Cart uses the named `pricing-queries` RequestBatcher pipeline
to combine product lookups before calling the Pricing Web API. Pricing reads seeded products directly from PostgreSQL.
Grafana OTel LGTM receives traces,
metrics, and logs from the two Web APIs.

The sample is a local observability demonstration, not a benchmark or production deployment. It has two .NET 10
business projects, Cart and Pricing, plus the `RequestBatcher.Observability.TrafficGenerator` console project. Each
traffic-generator request contains 2–10 products and emits no OpenTelemetry spans, so the Cart ASP.NET Core request span
is the root visible in the trace. Product IDs 1 through 100 are seeded by
PostgreSQL's initialization script.

## Start

Run these commands from the repository root:

```bash
# Build images, start services in dependency order, and wait for health checks.
docker compose -f samples/RequestBatcher.Observability/compose.yaml up --build -d --wait
# Inspect the running services and published ports.
docker compose -f samples/RequestBatcher.Observability/compose.yaml ps
```

The Compose build uses the repository root as its context. `up --build -d --wait` builds the application images, starts the stack in dependency order, and waits for health checks. The default loopback endpoints are:

| Service | URL | Purpose |
| --- | --- | --- |
| Cart | <http://127.0.0.1:5081> | Quote API and health endpoint |
| Pricing | <http://127.0.0.1:5082> | Product price API and health endpoint |
| Grafana | <http://127.0.0.1:3000> | Traces, metrics, logs, and dashboard |
| OTLP/gRPC | `http://127.0.0.1:4317` | Optional local OTLP receiver |
| OTLP/HTTP | `http://127.0.0.1:4318` | OTLP receiver used by the APIs |

Override `CART_PORT`, `PRICING_PORT`, `GRAFANA_PORT`, `OTLP_GRPC_PORT`, or `OTLP_HTTP_PORT` when a default is in use.
The traffic generator defaults to 32 workers and a 250 ms delay; override `TRAFFIC_CONCURRENCY` and
`TRAFFIC_DELAY_SECONDS` when starting the stack.

Docker runs the separate `CheckHealth.cs` application to probe each API's `/health` route before starting dependent
services. This checks HTTP availability, not database readiness. Business endpoints live in `CartEndpoints.cs` and
`PricingEndpoints.cs`; the `Program.cs` files compose and start their hosts.

The repository's .NET 10 SDK can also run the health-check file application directly. The traffic generator is a regular
project so Rider can build and run it:

```bash
dotnet run --project samples/RequestBatcher.Observability/TrafficGenerator/RequestBatcher.Observability.TrafficGenerator.csproj -- --help
dotnet run --project samples/RequestBatcher.Observability/TrafficGenerator/RequestBatcher.Observability.TrafficGenerator.csproj
dotnet run --file samples/RequestBatcher.Observability/CheckHealth.cs -- http://localhost:5081/health
```

Check both APIs and submit a manual quote:

```bash
curl --fail http://127.0.0.1:5081/health
curl --fail http://127.0.0.1:5082/health
curl --fail --request POST http://127.0.0.1:5081/cart/quote \
  --header 'Content-Type: application/json' \
  --data '[{"productId":1,"quantity":2},{"productId":2,"quantity":1}]'
```

The quote response contains one line per requested product and the calculated total. Cart validates 2 to 10 entries, positive
product IDs, and quantities from 1 through 100. Missing products return `404`; invalid input returns `400`.
Pricing also supports direct inspection and explicit grouped reads:

```bash
curl --fail http://127.0.0.1:5082/prices/1
curl --fail 'http://127.0.0.1:5082/prices?ids=1,2,3,64,100'
```

## Observability

Open the [RequestBatcher observability dashboard](http://127.0.0.1:3000/d/requestbatcher-observability/requestbatcher-observability)
and sign in with `admin` / `admin` if prompted. The HTTP panels show request rate and duration; the RequestBatcher panels
show submitted requests, batch size, and batch duration. The logs and trace panels show the two services' logs and Tempo
trace families for `RequestBatcher.Execute` and `RequestBatcher.ProcessBatch`.

The visible trace starts with the Cart ASP.NET Core request span. Each Cart request creates one
`RequestBatcher.Execute pricing-queries` span for its complete 2–10 ID sequence. The shared Cart batch handler receives up to 20
product requests, mixes requests from concurrent Cart calls, deduplicates their IDs, and makes one HTTP call to Pricing for the group. Pricing's ASP.NET Core span contains the
Npgsql database span created by Npgsql's official OpenTelemetry integration. The Cart batch spans are independent
Consumer roots linked to their contributing caller traces and carry `requestbatcher.name=pricing-queries`. A single Cart trace
has one `Execute` span, but an explicit 2–10 item submission can be split across multiple `ProcessBatch` spans; a batch can
also mix product requests from concurrent Cart traces. `requestbatcher.batch.size` records product requests in that batch.

To follow one call into a batch, open the Cart trace, copy the `Execute` span's trace ID, and search Tempo with:

```text
{ link:traceID = "CALL_TRACE_ID" && span:name =~ "RequestBatcher.ProcessBatch.*" }
```

The pinned Tempo build stores this query field without leading zeroes. Remove leading `0` characters from a copied
trace ID before substituting it above. Select a time range that includes the original call. A batch can contain fewer than
20 submissions because RequestBatcher does not wait to fill `BatchSize`.

Open the returned batch span to inspect the Cart-to-Pricing HTTP child and the Pricing database span. Activity links are
causal references rather than shared parent edges, so the reverse-link view depends on the tracing backend and does not imply one shared trace tree. See Grafana's
[TraceQL link query documentation](https://grafana.com/docs/tempo/latest/traceql/construct-traceql-queries/) for the
`link:traceID` and `link:spanID` selectors.

You can also inspect links directly in Grafana: open a `ProcessBatch` trace from the dashboard, click the
`RequestBatcher.ProcessBatch pricing-queries` span row, and expand `Links` in the span details. The panel lists the
`Execute` caller contexts that contributed to that batch. The `Execute` span does not expose outgoing links because the
links are recorded on the batch span; Grafana's [trace link view](https://grafana.com/docs/grafana/latest/visualizations/explore/trace-integration/#links)
and the TraceQL query above provide the reverse navigation.

![Cart and pricing business flow](business-flow.svg)

![RequestBatcher observability flow](../../docs/diagrams/observability.svg)

The [sample design](../../docs/observability-sample-design.md) documents the current flow and compatibility contract.
The [sample source](https://github.com/eventhorizon-cli/RequestBatcher/tree/main/samples/RequestBatcher.Observability)
shows the two Web APIs, the traffic generator, and their telemetry setup.

For optional end-to-end validation, see the [integration test guide](../../tests/it/README.md)
([中文](../../tests/it/README.zh-CN.md)). It reuses the running Compose stack and explicitly enables
`REQUESTBATCHER_RUN_INTEGRATION_TESTS=1` while checking one Cart `Execute` per HTTP request, cross-trace batch links,
Pricing/database parent-child spans, and metrics.

RequestBatcher is in-process and does not recover pending requests after a process failure.
