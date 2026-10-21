using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace RequestBatcher.Observability.Cart;

internal static class SampleTelemetry
{
    internal static void Configure(IHostApplicationBuilder builder)
    {
        var serviceName = builder.Configuration["OTEL_SERVICE_NAME"] ?? "requestbatcher-cart";
        builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(serviceName))
            .WithTracing(tracing => tracing
                .SetSampler(new AlwaysOnSampler())
                .AddAspNetCoreInstrumentation(options =>
                    options.Filter = context => !context.Request.Path.StartsWithSegments("/health"))
                .AddHttpClientInstrumentation()
                .AddOtlpExporter())
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
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
