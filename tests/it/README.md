# Observability integration tests

[English](README.md) | [简体中文](README.zh-CN.md)

These .NET 10 xUnit tests exercise the running [observability sample](../../samples/RequestBatcher.Observability/README.md)
through real HTTP, PostgreSQL, OTLP export, Tempo, and Prometheus. Like the
[EventHorizon.RocketMQ integration tests](https://github.com/eventhorizon-cli/EventHorizon.RocketMQ/tree/main/tests/it),
they live separately from unit tests and have explicit environment requirements. They reuse the sample's Compose
stack and never start, stop, or reset containers themselves. Ordinary unit tests require no external services.

From the repository root, build the sample images and wait for the stack to become healthy:

```bash
docker compose -f samples/RequestBatcher.Observability/compose.yaml up --build -d --wait
```

Enable and run the integration test:

```bash
REQUESTBATCHER_RUN_INTEGRATION_TESTS=1 dotnet test \
  tests/it/RequestBatcher.Observability.IntegrationTests/RequestBatcher.Observability.IntegrationTests.csproj \
  --configuration Release --logger 'console;verbosity=detailed'
```

Without `REQUESTBATCHER_RUN_INTEGRATION_TESTS=1`, the test is reported as skipped. The project is included in
`RequestBatcher.slnx`, so it can also be built and run from Rider. The existing traffic generator can keep running.

The test sends two bursts of 32 concurrent Cart requests with unique W3C trace contexts. Each request contains 2–10
product items, including a duplicate. It verifies:

- Response order, quantities, seeded prices, duplicate fan-out, and totals.
- Exactly one `RequestBatcher.Execute pricing-queries` span per Cart request, under its HTTP server span.
- Independent `RequestBatcher.ProcessBatch pricing-queries` roots with unique Links to the caller Execute spans,
  coverage of every test caller, and a batch combining multiple callers.
- Batch sizes of 1–20 **product items**. A Cart submission can span multiple batches; a batch need not be full.
- Cart HTTP client → Pricing HTTP server → Npgsql query parent relationships, successful HTTP status, and no Pricing batcher.
- New contexts in a subsequent burst, guarding against an earlier caller contaminating later traces.
- All five RequestBatcher instruments exported, with increasing counters and histogram counts/sums.

Trace and metric assertions poll for up to 120 seconds per phase because OTLP export, Tempo indexing, and Prometheus
scraping are asynchronous. Failures include missing trace/link/metric evidence. Successful output includes caller and
batch trace IDs for inspection in Grafana. Only this run's trace IDs are searched; unrelated sample traffic is allowed.
Metrics are cumulative for the named sample batcher, so additional generator traffic may also contribute to their growth.
The test uses the sample's seeded product IDs 1–100 and prices (`id × 1.25`); it does not modify database contents.

| Environment variable | Default | Purpose |
| --- | --- | --- |
| `REQUESTBATCHER_RUN_INTEGRATION_TESTS` | unset | Set to `1` to enable live tests. |
| `REQUESTBATCHER_CART_URL` | `http://127.0.0.1:5081` | Cart address, matching any Compose `CART_PORT` override. |
| `REQUESTBATCHER_GRAFANA_URL` | `http://127.0.0.1:3000` | Grafana address, matching any `GRAFANA_PORT` override. |
| `REQUESTBATCHER_GRAFANA_USER` | `admin` | Local Grafana user. |
| `REQUESTBATCHER_GRAFANA_PASSWORD` | `admin` | Local Grafana password. |

The tests use the provisioned `requestbatcher-tempo` and `requestbatcher-prometheus` data-source UIDs through Grafana's
authenticated proxy. No additional backend ports or test-only production endpoints are needed.
