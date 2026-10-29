namespace RequestBatcher.Diagnostics;

internal sealed class RequestBatcherTraceMetadata
{
    internal RequestBatcherTraceMetadata(string requestTypeName, string? batcherName = null)
    {
        CallName = RequestBatcherTracing.GetCallName(batcherName);
        KeyValuePair<string, object?>[] tags = batcherName is null
            ? [new(RequestBatcherTraceTags.RequestType, requestTypeName)]
            :
            [
                new(RequestBatcherTraceTags.RequestType, requestTypeName),
                new(RequestBatcherTraceTags.BatcherName, batcherName),
            ];
        Tags = Array.AsReadOnly(tags);
    }

    internal string CallName { get; }

    internal IEnumerable<KeyValuePair<string, object?>> Tags { get; }
}
