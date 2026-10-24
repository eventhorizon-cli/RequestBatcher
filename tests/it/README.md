# Observability integration tests

[English](README.md) | [简体中文](README.zh-CN.md)

These .NET 10 xUnit tests exercise the [observability sample](../../samples/RequestBatcher.Observability/README.md)
through real HTTP, PostgreSQL, OTLP export, Tempo, and Prometheus. Like the
[EventHorizon.RocketMQ.EventBus integration tests](https://github.com/eventhorizon-cli/EventHorizon.RocketMQ.EventBus/tree/main/tests/it),
they live separately from unit tests and have explicit environment requirements. The live fixture creates an isolated
Testcontainers network, builds the Cart and Pricing sample images, starts PostgreSQL 17.6 and OTEL-LGTM 0.29.0, and
assigns random host ports. It tears down containers, images, and the network after the test, including partial startup.
Testcontainers' resource reaper is an additional cleanup guard. Ordinary unit tests require no external services.

Enable and run the integration test:

```bash
REQUESTBATCHER_RUN_INTEGRATION_TESTS=1 dotnet test \
  tests/it/RequestBatcher.Observability.IntegrationTests/RequestBatcher.Observability.IntegrationTests.csproj \
  --configuration Release \
  --collect:"XPlat Code Coverage" \
  --settings tests/it/RequestBatcher.Observability.IntegrationTests/coverage.runsettings \
  --results-directory TestResults/Integration \
  --logger 'console;verbosity=detailed'
```

The coverage settings include the `RequestBatcher` and `RequestBatcher.OpenTelemetry` production assemblies; sample and test assemblies are excluded.
This collects coverage from the in-process stress tests. Code executing inside the sample containers is not instrumented
by the test host's collector; the live test verifies exported behavior separately. CI uploads this report with the
`integration` flag and unit-test reports with the `unit` flag.

The same project also contains in-process tests that use the public API and the real OpenTelemetry SDK without Docker or
the environment switch: `ExecuteAsync_ConcurrentWavesAtEachSamplingRate_ProcessesExactlyOnceAndLinksActualBatches`
checks 32 waves of 512 requests (16,384 requests per sampling mode) at 0% and 100% sampling, with mixed single and
16-request submissions, an asynchronous 1 ms handler delay, bounded pending capacity, and per-wave telemetry cleanup.
These baseline waves register `AddRequestBatcherInstrumentation()` so the linked-batch policy is exercised with the
application's sampler configuration.
`ExecuteAsync_Instrumentation_MixedSamplingLinksAllParticipatingContexts` checks that a mixed batch links to all participating
Execute contexts, including unsampled contexts, with the application's sampler configuration preserved; Handle export
is asserted from whether the actual batch contains a sampled Execute context.
`ExecuteAsync_Instrumentation_SamplesBatchWithZeroRootRatio` registers the parameterless
`AddRequestBatcherInstrumentation()` extension and checks that a sampled Execute causes its Handler batch to be sampled
even with a 0% root ratio. Deterministic unit tests cover dropping a batch when all Execute spans are unsampled through
the extension and preserving application sampling with direct source registration.
`DisposeAsync_QueuedCancellationAndHandlerFailure_DrainsAcceptedWorkAndRejectsLateSubmissions` checks cancellation,
draining, failures, and late admission. Run the project without `REQUESTBATCHER_RUN_INTEGRATION_TESTS` to execute these
tests without starting the live fixture.

Without `REQUESTBATCHER_RUN_INTEGRATION_TESTS=1`, the live test is reported as skipped. The project is included in
`RequestBatcher.slnx`, so it can also be built and run from Rider. The fixture owns the test environment and supplies
the generated Cart and Grafana URLs; no fixed-port environment variables are required. For a manually inspected
sample, use the separate Compose instructions in the sample README and stop that stack when finished.

The test sends two bursts of 32 concurrent Cart requests with unique W3C trace contexts. Each request contains 2–10
product items, including a duplicate. It verifies:

- Response order, quantities, seeded prices, duplicate fan-out, and totals.
- Exactly one `RequestBatcher.Execute pricing-queries` span per Cart request, under its HTTP server span.
- Independent `RequestBatcher.Handle pricing-queries` roots with unique links from each batch to its caller
  `Execute` spans, matching links from each `Execute` span to the `Handle` spans that handled its requests,
  coverage of every test caller, and a batch combining multiple callers.
- Batch sizes of 1–20 **product items**. A Cart submission can span multiple batches; a batch need not be full.
- Cart HTTP client → Pricing HTTP server → Npgsql query parent relationships, successful HTTP status, and no Pricing batcher.
- New contexts in a subsequent burst, guarding against an earlier caller contaminating later traces.
- All five RequestBatcher instruments exported with the short request type `Int32`, with increasing counters and histogram counts/sums.

The sample uses `service.name=cart` and `service.name=pricing` for new telemetry. The dashboard's `Cart calls · Execute`
and `Handler execution · Handle` tables expose both directions explicitly: a `Handle` row links to its caller
`Execute` spans, and an `Execute` row links to the `Handle` spans that handled its requests. Data written before this naming change keeps the old service names until
backend retention removes it.

Trace and metric assertions poll for up to 120 seconds per phase because OTLP export, Tempo indexing, and Prometheus
scraping are asynchronous. Failures include missing trace/link/metric evidence. Successful output includes caller and
batch trace IDs for inspection in Grafana. Only this run's trace IDs are searched; unrelated sample traffic is allowed.
Metrics are cumulative for the named sample batcher, so additional generator traffic may also contribute to their growth.
The test uses the sample's seeded product IDs 1–100 and prices (`id × 1.25`); it does not modify database contents.

| Environment variable | Default | Purpose |
| --- | --- | --- |
| `REQUESTBATCHER_RUN_INTEGRATION_TESTS` | unset | Set to `1` to enable live tests. |

The fixture uses the sample's default Grafana credentials (`admin` / `admin`) and generates the Cart and Grafana host
addresses for each run.

The tests use the provisioned `requestbatcher-tempo` and `requestbatcher-prometheus` data-source UIDs through Grafana's
authenticated proxy. No additional backend ports or test-only production endpoints are needed.
