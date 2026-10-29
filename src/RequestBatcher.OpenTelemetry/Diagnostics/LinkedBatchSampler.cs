using System.Diagnostics;
using global::OpenTelemetry.Trace;
using RequestBatcher.Diagnostics;

namespace RequestBatcher.OpenTelemetry.Diagnostics;

internal sealed class LinkedBatchSampler : Sampler, IDisposable
{
    private const string BatchName = RequestBatcherTracing.BatchName;
    private readonly Sampler _inner;
    private int _disposed;

    internal LinkedBatchSampler(Sampler inner)
    {
        _inner = inner;
        Description = $"RequestBatcherLinkedBatches{{{inner.Description}}}";
    }

    public override SamplingResult ShouldSample(in SamplingParameters samplingParameters)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return new SamplingResult(SamplingDecision.Drop);
        }

        if (!IsHandlerBatch(samplingParameters))
        {
            // Execute spans and Handler children keep the application's sampler, including its parent-based rules.
            return _inner.ShouldSample(samplingParameters);
        }

        // These are creation-time links to Execute contexts, not links read from an already allocated Handler Activity.
        // One sampled Execute is enough to sample the batch; do not mutate any caller or Activity sampling flags.
        if (samplingParameters.Links is { } links)
        {
            foreach (var link in links)
            {
                if ((link.Context.TraceFlags & ActivityTraceFlags.Recorded) != 0)
                {
                    return new SamplingResult(SamplingDecision.RecordAndSample);
                }
            }
        }

        // No sampled Execute context (including no links) means an unsampled Handler batch. Drop does not require
        // StartActivity to return null: the SDK may still create an Activity that carries only propagation context.
        return new SamplingResult(SamplingDecision.Drop);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            (_inner as IDisposable)?.Dispose();
        }
    }

    private static bool IsHandlerBatch(in SamplingParameters parameters)
    {
        // SamplingParameters exposes no ActivitySource, so match the batch's operation, kind, parent and creation tags.
        if (parameters.Kind != ActivityKind.Consumer || parameters.ParentContext.TraceId != default ||
            (parameters.Name != BatchName && !parameters.Name.StartsWith(BatchName + " ", StringComparison.Ordinal)))
        {
            return false;
        }

        var hasBatchSize = false;
        var hasRequestType = false;
        if (parameters.Tags is { } tags)
        {
            foreach (var tag in tags)
            {
                hasBatchSize |= tag.Key == RequestBatcherTraceTags.BatchSize && tag.Value is int size && size > 0;
                hasRequestType |= tag.Key == RequestBatcherTraceTags.RequestType && tag.Value is string;
                if (hasBatchSize && hasRequestType)
                {
                    return true;
                }
            }
        }

        return false;
    }
}
