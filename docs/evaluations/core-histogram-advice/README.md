# Core histogram advice validation

This validates the container-owned core metrics service and migration of size-histogram boundaries from OpenTelemetry
views to `InstrumentAdvice<long>`. It includes the existing backlog gauge changes and BufferQueue 2.2.0 in the working
tree. This is a functionality validation, not a before/after performance comparison or optimization claim.
The measured production source is commit `1f5a54c`. BufferQueue 2.2.0 package metadata identifies upstream commit
`56af9b0051d8b965905e243b78ce91ebc7f85dea`.

## Environment and workload

Measured on 2026-10-30 with SDK 10.0.100, .NET 10.0.0, macOS 27.2.0, Arm64, Release configuration, and
OpenTelemetry 1.20.1. Each case uses five warmup waves followed by 30 measured waves of 1,024 unique requests,
BatchSize 32, concurrency eight, and simulated 20 ms Handler I/O. Single submissions and explicit groups of 32 are
measured in separate processes with tracing disabled. The enabled mode aggregates with the SDK and collects at cleanup;
it does not measure periodic collection, OTLP transport, or backend costs. No build or test ran alongside measured waves.

| Submission | Metrics | Mean per wave (ms) | Allocated bytes per item |
| --- | --- | ---: | ---: |
| Single | Disabled | 104.566 | 282.830 |
| Single | Aggregation only | 104.854 | 538.394 |
| Explicit groups | Disabled | 84.963 | 169.120 |
| Explicit groups | Aggregation only | 85.230 | 177.714 |

These are bounded current-source measurements, including runtime scheduling and batch-formation variation. They do
not isolate the cost of moving bucket configuration into core. Container startup and bucket calculation occur outside
the measured region. JSON files retain runtime, allocation, and wave-duration summaries; [validation.txt](validation.txt)
retains correctness and exported totals.

## Correctness

All four cases validated each request's exactly-once handling across 35 waves (35,840 items per case). Enabled cases
exported all four histograms plus the backlog gauge. Submission and batch sums each equaled 35,840; submission and
Execute counts equaled 35,840 for singles and 1,120 for explicit groups. Handler duration counts matched actual batch
counts. The final sampled backlog was zero.

Unit coverage checks small and maximum integer BatchSize, overflow, native Advice visibility, direct SDK aggregation,
explicit View precedence, independent container Advice and disposal, and all registration shapes. Public API integration
coverage combines ordinary and delegate response pipelines with BatchSize 40/120 and verifies response order,
exactly-once handling, raw measurements, exported boundaries, bucket counts, sums, and tags.

Final validation passed 578 unit tests on each of net8.0 and net10.0, plus six .NET 10 Advice/tracing integration
cases without Docker. The scoped-response tracing test now validates every actual Handler batch rather than assuming
one explicit submission always forms one batch. Solution build completed without warnings or errors; formatting and
package checks passed. The core package declares DiagnosticSource directly only for net8.0.

The final live run also passed all seven integration cases in 4.09 minutes. For each ordinary/delegate pipeline,
64 calls containing 364 product items exported all four histograms and the backlog gauge through OTLP to Prometheus,
with linked traces verified in Tempo. The fixture removed its containers, images, and network; a subsequent Docker
container listing was empty. Run the integration project without a filter and with `REQUESTBATCHER_RUN_INTEGRATION_TESTS=1` to enable
this Docker-backed validation.

## Review follow-up

The 2026-10-30 working-tree review removes the shared call-diagnostics facade and dependency-forwarding properties,
makes Metrics a required container-owned dependency, and moves backlog registration into the consumer loop. Metrics
uses one configuration-based constructor and asynchronous disposal only. Disposal waits for already-started Execute
measurements to record before releasing the Meter; previously, queue draining could finish before those continuations.

All 588 unit cases passed on each target framework, including eight shutdown cases and two consumer-loop cleanup cases.
The shutdown stress case records all 25,600 Execute observations during repeated container disposal. Mixed response
calls validate results and ordering for typed and service-free delegate handlers. The six in-process SDK cases passed,
as did the live OTLP/Prometheus/Tempo test in 2 minutes 8 seconds. Docker listed no remaining containers afterward.
Solution build and formatting checks passed. Independent source review and Chinese documentation review found no
remaining blocking issue after their documentation and test-cleanup findings were addressed.

Root-provider disposal prevents creating new DI scopes. Consequently, callers using scoped handlers or service-injected
delegates must stop submitting and await outstanding calls before disposing the provider. The shutdown metrics tests
isolate this existing scope constraint rather than weakening the exact observation-count assertions.

The same .NET 10.0.0, Release, 5-warmup/30-measured-wave probe was rerun after the review fixes, with no Docker,
build, or tests running alongside measured waves. The final run includes the DI/metadata and single-task monitoring changes,
before the final handler namespace-only move. These measurements are not a controlled baseline
comparison or a performance-improvement claim. Each case validated 35,840 items; enabled cases checked all four
histograms and a final zero backlog. See `review-*.json` and [review-validation.txt](review-validation.txt).

| Submission | Metrics | Mean per wave (ms) | Allocated bytes per item |
| --- | --- | ---: | ---: |
| Single | Disabled | 102.838 | 285.660 |
| Single | Aggregation only | 103.674 | 534.373 |
| Explicit groups | Disabled | 84.268 | 169.535 |
| Explicit groups | Aggregation only | 85.132 | 177.528 |

The DI cleanup registers coordinators and adapters by type, shares immutable batcher metadata, and makes
the coordinator internal. Telemetry and logs use short type names; disposed-object exceptions retain their full object
names. After these changes and the code naming cleanup, the Release solution build passed with zero warnings/errors,
all 588 unit cases passed on each of net8.0/net10.0, and all six in-process SDK cases passed on net10.0. Formatting and
`git diff --check` passed. One old tracing fixture now uses its own callback delegate rather than registering the
production execution-delegate type; another old assertion now verifies exactly-once processing without assuming one
explicit submission becomes one handler batch.

The bounded Compose validation loaded the provisioned Grafana dashboard with separate HTTP, Pricing database and
RequestBatcher rows. All 25 Prometheus panel queries returned nonempty successful results. Both ordinary and delegate
batcher names were present, and the displayed Batcher charts were inspected in Chrome. The connection-pool state label
was corrected against live exported series. The Compose stack was shut down afterward; `compose ps --all` was empty.
The timing/allocation probes above validate the final runtime changes without asserting a performance improvement.

Final structural validation after organizing `Diagnostics` and `Handlers` passed the Release build, formatting, both
package builds, all 586 unit cases on each target framework, and six in-process SDK cases on .NET 10. The unit count
reflects removal of six tests tied to the deleted monitor class and addition of four coordinator-level cases for
unexpected exit and failure-callback context isolation. The nine focused monitoring/context cases pass on both
frameworks. Coordinator shutdown waits for monitoring before inspecting its recorded failure; unexpected cancellation
now reports the same infrastructure failure to queued requests and disposal. Partition configuration remains unchanged.
Independent PR review also removed a one-submission/one-handler-batch assumption from the SDK advice test. The test now
forces an 80-item submission through BatchSize 40, compares native and exported histogram buckets with independently
captured handler batches, and retains exactly one submission measurement. All six SDK cases pass with that regression.

## Commands

Run from the repository root; repeat the measurement command for `Disabled`/`AggregationOnly` and `single`/`multi`,
choosing a separate output path for each case. Subsequent runs may use `--no-build` instead of `--no-cache`.

```bash
dotnet run --configuration Release --file scripts/MeasureMetricsOverhead.cs --no-cache \
  --disable-build-servers --artifacts-path /tmp/requestbatcher-advice-build -- \
  Disabled single docs/evaluations/core-histogram-advice/disabled-single.json 30
dotnet test tests/RequestBatcher.Tests/RequestBatcher.Tests.csproj --configuration Release
dotnet test tests/it/RequestBatcher.Observability.IntegrationTests/RequestBatcher.Observability.IntegrationTests.csproj \
  --configuration Release --filter 'FullyQualifiedName~MetricsAdviceTests|FullyQualifiedName~TracingStressTests'
```
