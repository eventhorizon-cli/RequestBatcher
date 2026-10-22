using Npgsql;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace RequestBatcher.Observability.Pricing;

internal static class SampleTelemetry
{
    internal static void Configure(IHostApplicationBuilder builder, string defaultServiceName)
    {
        var serviceName = builder.Configuration["OTEL_SERVICE_NAME"] ?? defaultServiceName;
        builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(serviceName))
            .WithTracing(tracing => tracing
                // Keep every call and independent batch visible in this local demonstration.
                .SetSampler(new AlwaysOnSampler())
                .AddNpgsql()
                .AddAspNetCoreInstrumentation(options =>
                    options.Filter = context => !context.Request.Path.StartsWithSegments("/health"))
                .AddOtlpExporter())
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddMeter("Npgsql")
                .AddOtlpExporter());
        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.SetResourceBuilder(ResourceBuilder.CreateDefault().AddService(serviceName));
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
            logging.AddOtlpExporter();
        });
    }
}
