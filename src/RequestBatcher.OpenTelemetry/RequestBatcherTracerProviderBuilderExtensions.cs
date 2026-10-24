using RequestBatcher.OpenTelemetry.Diagnostics;

namespace OpenTelemetry.Trace;

/// <summary>
/// Provides OpenTelemetry registration extensions for RequestBatcher.
/// </summary>
public static class RequestBatcherTracerProviderBuilderExtensions
{
    /// <summary>
    /// Registers RequestBatcher spans and samples handler batches according to their linked Execute contexts.
    /// </summary>
    /// <param name="builder">The tracer provider builder to configure.</param>
    /// <remarks>
    /// A handler batch is sampled if at least one linked Execute context is sampled and dropped otherwise.
    /// Execute spans and handler child spans retain the application's sampler. To preserve application sampling for
    /// every span, register the RequestBatcher ActivitySource directly with AddSource("RequestBatcher") instead.
    /// Sampling inspects the links supplied when the handler Activity is created; it does not change sampling flags
    /// after creation. A dropped handler batch can still have an unsampled Activity for context propagation, depending
    /// on the SDK and other registered listeners. Unsampled batches are not guaranteed to allocate no Activity objects.
    /// </remarks>
    /// <returns>The same builder instance for fluent configuration.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="builder" /> is null.</exception>
    public static TracerProviderBuilder AddRequestBatcherInstrumentation(this TracerProviderBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ConfigureSampler(static (_, sampler) =>
            sampler is LinkedBatchSampler ? sampler : new LinkedBatchSampler(sampler));

        return builder.AddSource("RequestBatcher");
    }
}
