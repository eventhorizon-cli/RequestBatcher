namespace OpenTelemetry.Metrics;

/// <summary>
/// Provides OpenTelemetry registration extensions for RequestBatcher metrics.
/// </summary>
public static class RequestBatcherMeterProviderBuilderExtensions
{
    /// <summary>
    /// Registers RequestBatcher meters with the supplied meter provider builder.
    /// </summary>
    /// <param name="builder">The meter provider builder to configure.</param>
    /// <returns>The same builder instance for fluent configuration.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="builder" /> is null.</exception>
    public static MeterProviderBuilder AddRequestBatcherInstrumentation(this MeterProviderBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.AddMeter("RequestBatcher");
    }
}
