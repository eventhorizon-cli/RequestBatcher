using Microsoft.Extensions.DependencyInjection;
using RequestBatcher.OpenTelemetry.Diagnostics;

namespace OpenTelemetry.Metrics;

/// <summary>
/// Provides OpenTelemetry registration extensions for RequestBatcher metrics.
/// </summary>
public static class RequestBatcherMeterProviderBuilderExtensions
{
    /// <summary>
    /// Registers RequestBatcher meters with the supplied meter provider builder.
    /// </summary>
    /// <remarks>
    /// At provider construction, size histogram buckets are derived from the largest BatchSize snapshot among
    /// RequestBatcher pipelines registered in the same service collection. Bucket counts are bounded independently of
    /// pipeline count; submission-size buckets also cover multiples of BatchSize. Duration buckets retain SDK defaults.
    /// Register only the Meter with
    /// AddMeter("RequestBatcher") to supply custom views instead. Without pipeline metadata, SDK defaults are retained.
    /// </remarks>
    /// <param name="builder">The meter provider builder to configure.</param>
    /// <returns>The same builder instance for fluent configuration.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="builder" /> is null.</exception>
    public static MeterProviderBuilder AddRequestBatcherInstrumentation(this MeterProviderBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ConfigureServices(services =>
        {
            if (services.Any(descriptor => descriptor.ServiceType == typeof(RequestBatcherMeterProviderConfiguration)))
            {
                return;
            }

            services.AddSingleton(new RequestBatcherMeterProviderConfiguration());
            services.ConfigureOpenTelemetryMeterProvider(static (provider, meterBuilder) =>
                provider.GetRequiredService<RequestBatcherMeterProviderConfiguration>().ConfigureBuilder(provider, meterBuilder));
        });
        return builder.AddMeter("RequestBatcher");
    }
}
