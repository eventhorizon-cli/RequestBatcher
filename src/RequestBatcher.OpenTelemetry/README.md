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
`.WithMetrics(metrics => metrics.AddRequestBatcherInstrumentation())`. Both register the same Meter; the extension also
applies size-histogram views from the largest `BatchSize` registered in the same service collection when `MeterProvider`
is built. Complete batcher registrations first. Direct `AddMeter("RequestBatcher")` registers the Meter without these
views and retains SDK defaults.

The Meter exposes four histograms: `requestbatcher.submission.size` and `requestbatcher.batch.size` are `Histogram<long>`
in `{request}`; `requestbatcher.handle.duration` and `requestbatcher.execute.duration` are `Histogram<double>` in `ms`.
For recording semantics, tags, PromQL, bucket rules, and MeterListener usage, see the
[RequestBatcher Metrics guide](https://github.com/eventhorizon-cli/RequestBatcher/blob/main/docs/metrics.md).

## Migration

The registration extensions moved from the core package to `RequestBatcher.OpenTelemetry`. Existing users of
`AddRequestBatcherInstrumentation()` must add this package and rebuild. The extension namespaces and method names remain
the same, including the parameterless tracing method. Users who register `AddSource("RequestBatcher")` and
`AddMeter("RequestBatcher")` directly do not need this package.

The core and integration packages are versioned independently. Their version numbers do not need to match.
