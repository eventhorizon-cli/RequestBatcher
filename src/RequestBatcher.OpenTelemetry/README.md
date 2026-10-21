# RequestBatcher.OpenTelemetry

[![NuGet](https://img.shields.io/nuget/v/RequestBatcher.OpenTelemetry.svg)](https://www.nuget.org/packages/RequestBatcher.OpenTelemetry)

`RequestBatcher.OpenTelemetry` registers RequestBatcher's built-in `ActivitySource` with an OpenTelemetry tracer
provider. The package targets .NET 8 and .NET 10 and does not create a provider, select an exporter, or change the
application's sampling policy.

```bash
dotnet add package RequestBatcher.OpenTelemetry
```

## Observability

Register the adapter alongside the OpenTelemetry host integration and the exporter selected by the application:

```csharp
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Trace;

services.AddOpenTelemetry()
    .WithTracing(tracing => tracing
        .AddRequestBatcherInstrumentation()
        .AddOtlpExporter());
```

The application supplies `OpenTelemetry.Extensions.Hosting` for `AddOpenTelemetry` and the exporter package used by
the application. `RequestBatcher.OpenTelemetry` only provides the `AddRequestBatcherInstrumentation` registration
method.

![RequestBatcher observability flow](https://raw.githubusercontent.com/eventhorizon-cli/RequestBatcher/main/docs/diagrams/observability.svg)

Each public submission creates an `Internal` `RequestBatcher.Execute` span that measures the complete caller-visible
operation, including queue and capacity waiting. Each dispatched nonempty handler batch creates a `Consumer`
`RequestBatcher.ProcessBatch` span in an isolated background context. The batch span links to the submission spans
that contributed requests, so one submission can be linked to multiple batches and one batch can link to multiple
submissions. Downstream database and HTTP spans created by the handler are children of the batch span.

Tracing remains best effort: sampling and exporter retention are controlled by the application's OpenTelemetry
configuration. RequestBatcher is in-process and does not recover queued requests after process failure.

See the [observability design](https://github.com/eventhorizon-cli/RequestBatcher/blob/main/docs/observability-design.md)
for the complete span and correlation contract.
