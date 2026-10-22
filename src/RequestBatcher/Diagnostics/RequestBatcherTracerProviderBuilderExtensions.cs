namespace OpenTelemetry.Trace;

/// <summary>
/// Provides OpenTelemetry registration extensions for RequestBatcher.
/// </summary>
public static class RequestBatcherTracerProviderBuilderExtensions
{
    /// <summary>
    /// Registers RequestBatcher spans with the supplied tracer provider builder.
    /// </summary>
    /// <param name="builder">The tracer provider builder to configure.</param>
    /// <returns>The same builder instance for fluent configuration.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="builder" /> is null.</exception>
    public static TracerProviderBuilder AddRequestBatcherInstrumentation(this TracerProviderBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.AddSource("RequestBatcher");
    }
}
