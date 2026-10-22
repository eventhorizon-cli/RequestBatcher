namespace RequestBatcher.Diagnostics;

internal sealed class RequestBatcherTraceMetadata
{
    internal RequestBatcherTraceMetadata(string requestTypeName, string? batcherName = null)
    {
        CallName = RequestBatcherTracing.GetCallName(batcherName);
        KeyValuePair<string, object?>[] tags = batcherName is null
            ? [new("requestbatcher.request.type", requestTypeName)]
            :
            [
                new("requestbatcher.request.type", requestTypeName),
                new("requestbatcher.name", batcherName),
            ];
        Tags = Array.AsReadOnly(tags);
    }

    internal string CallName { get; }

    internal IEnumerable<KeyValuePair<string, object?>> Tags { get; }
}
