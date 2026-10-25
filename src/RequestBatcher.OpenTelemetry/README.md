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
exporter configuration. Registering tracing decorates the application's sampler for RequestBatcher Handle batch roots.

## Registration

Register the source and meter with the application's existing providers:

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

Installing [`RequestBatcher.OpenTelemetry`](https://www.nuget.org/packages/RequestBatcher.OpenTelemetry) alone does not register tracing. The example calls `.AddRequestBatcherInstrumentation()` to
register the stable `RequestBatcher` ActivitySource and decorate the application's sampler with linked-batch sampling.
The extension samples a `Handle` root whenever any creation-time `Handle-to-Execute` link
context has the `Recorded` flag and returns `Drop` when every participating `Execute` context is unsampled. Other
Activities, including `Execute` and handler child spans, still use the application's sampler. Applications that want
the original sampler to govern batch roots can register `.AddSource("RequestBatcher")` directly instead; a sampled
`Execute` may then have an unsampled batch and downstream children.

## Linked-Batch Sampling

Register the tracing extension when the application wants a `Handle` root to be retained whenever at least one
participating `Execute` context is sampled. The package must be installed and registered explicitly:

```csharp
.WithTracing(tracing => tracing
    .AddRequestBatcherInstrumentation()
    .AddOtlpExporter())
```

The extension decorates the sampler selected by application code or environment configuration through the SDK's public
`ConfigureSampler` hook. It works whether `SetSampler` is registered before or after the extension. For a recognized
`RequestBatcher.Handle` Consumer root, including named operation forms and the existing request-type and batch-size
creation tags, the decorator returns `RecordAndSample` when any creation-time `Handle-to-Execute` link context has
the `Recorded` flag. If every participating `Execute` context is unsampled, it returns `Drop`. A mixed batch retains all
distinct valid participating `Execute` contexts that entered processing, including unsampled contexts. If no Execute
Activity was created, a valid caller context can provide that submission's context. The batch remains an independent
root; no parent is created from a link. Other Activities, including `Execute` and handler child spans, continue through
the application's sampler. ParentBased child sampling follows the batch's sampled flag; arbitrary application samplers
may make their own decisions. The extension does not write Activity flags after creation.

Repeated registration is idempotent. SDK suppression,
processor/exporter decisions, and link delivery limits still apply, so `RecordAndSample` does not guarantee backend
delivery.

For compatibility, custom span-name queries should use `RequestBatcher.Handle` and append a named-registration suffix
after a space, such as `RequestBatcher.Handle pricing-queries`. The `RequestBatcher` ActivitySource and Meter names
are stable. Historical backend spans retain their previous names until backend retention removes them.

## Metrics

If only metrics are needed, you can choose to subscribe to the `RequestBatcher` Meter directly without installing this
package:

```csharp
builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics => metrics.AddMeter("RequestBatcher"));
```

You can also install [`RequestBatcher.OpenTelemetry`](https://www.nuget.org/packages/RequestBatcher.OpenTelemetry) and use
`.WithMetrics(metrics => metrics.AddRequestBatcherInstrumentation())`.
Both options register the same Meter and are supported independently of tracing.

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
