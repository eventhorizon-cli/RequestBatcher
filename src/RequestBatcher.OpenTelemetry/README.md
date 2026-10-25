# RequestBatcher.OpenTelemetry

`RequestBatcher.OpenTelemetry` contains the optional OpenTelemetry provider-builder extensions for RequestBatcher.
The core [`RequestBatcher`](https://www.nuget.org/packages/RequestBatcher) package uses the built-in .NET `ActivitySource`
and `Meter` APIs and has no OpenTelemetry package dependency. See the
[`RequestBatcher.OpenTelemetry` NuGet package](https://www.nuget.org/packages/RequestBatcher.OpenTelemetry).

## Install

```bash
dotnet add package RequestBatcher
dotnet add package RequestBatcher.OpenTelemetry
dotnet add package OpenTelemetry.Extensions.Hosting
dotnet add package OpenTelemetry.Instrumentation.AspNetCore
dotnet add package OpenTelemetry.Exporter.OpenTelemetryProtocol
```

The package targets net8.0 and net10.0. It declares a minimum compatible `RequestBatcher` dependency and requires
OpenTelemetry 1.20.1 or later, with no exact-version or upper-version constraint. It uses the application's provider and
exporter configuration; it does not create a provider or select an exporter.

## Registration

Register RequestBatcher with the application's existing tracing and metrics providers:

```csharp
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing
        .AddAspNetCoreInstrumentation()
        .AddRequestBatcherInstrumentation()
        .AddOtlpExporter())
    .WithMetrics(metrics => metrics
        .AddRequestBatcherInstrumentation()
        .AddOtlpExporter());
```

Installing [`RequestBatcher.OpenTelemetry`](https://www.nuget.org/packages/RequestBatcher.OpenTelemetry) alone does not register tracing. Add
`.AddRequestBatcherInstrumentation()` to the tracing provider to register the stable `RequestBatcher` ActivitySource and
enable the sampling behavior below. The metrics extension registers the `RequestBatcher` Meter.

## Linked-Batch Sampling

The application's sampler decides whether each `Execute` span is sampled. With the tracing extension registered:

| Execute spans represented in a Handler batch | Handle sampling |
| --- | --- |
| At least one is sampled | Sample the Handler batch. |
| All are unsampled | Do not sample the Handler batch. |

`Handle` remains an independent trace root. Handler child spans continue through the application's sampler; with a
ParentBased sampler, those children follow `Handle`. Named instances use the same behavior.

You can choose direct `.AddSource("RequestBatcher")` registration without this package. The application's sampler then
decides `Handle` sampling independently, so a sampled `Execute` may lack its corresponding recorded Handler trace.

For the current contract and detailed tracing flow, see the
[observability design](https://github.com/eventhorizon-cli/RequestBatcher/blob/main/docs/observability-design.md).

Span names are `RequestBatcher.Execute` and `RequestBatcher.Handle`. Named instances append the registered name after a
space, such as `RequestBatcher.Handle pricing-queries`.

## Metrics

If only metrics are needed, you can choose direct `RequestBatcher` Meter registration without installing this package:

```csharp
builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics => metrics.AddMeter("RequestBatcher"));
```

You can also install [`RequestBatcher.OpenTelemetry`](https://www.nuget.org/packages/RequestBatcher.OpenTelemetry) and use
`.WithMetrics(metrics => metrics.AddRequestBatcherInstrumentation())`. Both options register the same Meter and are
independent of tracing; the metrics extension only adds the meter.

Applications using `System.Diagnostics.Metrics.MeterListener` instead of the SDK do not need this package. Filter
`InstrumentPublished` by `instrument.Meter.Name == "RequestBatcher"` and call `EnableMeasurementEvents(instrument)` once
per desired instrument per listener. Discovery notifications and measurement callback registration alone do not enable
measurements. Register `long` callbacks for counts and batch size, and a `double` callback for batch duration. Start the
listener before submitting work and keep it alive until the batcher has shut down; subscriptions do not replay earlier
measurements. Recording invokes callbacks synchronously and potentially concurrently, so keep callbacks lightweight,
thread-safe, and prevent exceptions from escaping. Copy any tags needed after the callback returns. Custom listeners
provide their own aggregation and export. See the core package's
[metrics guidance](https://www.nuget.org/packages/RequestBatcher#readme-body-tab) for a listener example.

## Migration

The registration extensions moved from the core package to `RequestBatcher.OpenTelemetry`. Existing users of
`AddRequestBatcherInstrumentation()` must add this package and rebuild. The extension namespaces and method names remain
the same, including the parameterless tracing method. Users who register `AddSource("RequestBatcher")` and
`AddMeter("RequestBatcher")` directly do not need this package.

The core and integration packages are versioned independently. Their version numbers do not need to match.
