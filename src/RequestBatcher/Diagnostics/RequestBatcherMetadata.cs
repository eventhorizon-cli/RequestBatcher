using RequestBatcher.Diagnostics.Tracing;

namespace RequestBatcher.Diagnostics;

internal sealed class RequestBatcherMetadata
{
    public RequestBatcherMetadata(string requestTypeName, string? batcherName = null)
    {
        RequestTypeName = requestTypeName;
        BatcherName = batcherName;
        TraceCallName = RequestBatcherTracing.GetCallName(batcherName);
        KeyValuePair<string, object?>[] tags = batcherName is null
            ? [new(RequestBatcherTraceTags.RequestType, requestTypeName)]
            :
            [
                new(RequestBatcherTraceTags.RequestType, requestTypeName),
                new(RequestBatcherTraceTags.BatcherName, batcherName),
            ];
        TraceTags = Array.AsReadOnly(tags);
    }

    public string RequestTypeName { get; }
    public string? BatcherName { get; }
    public string TraceCallName { get; }

    public IEnumerable<KeyValuePair<string, object?>> TraceTags { get; }
}
