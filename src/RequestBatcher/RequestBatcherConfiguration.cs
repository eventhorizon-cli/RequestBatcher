
using RequestBatcher.Diagnostics;

namespace RequestBatcher;

internal sealed class RequestBatcherConfiguration<TRequest>(
    string topicName,
    string? batcherName = null,
    RequestBatchOptions<TRequest>? options = null)
{
    public string TopicName { get; } = topicName;
    public RequestBatchOptions<TRequest>? Options { get; } = options;
    public RequestBatcherMetadata Metadata { get; } = new(
        RequestBatcherRequestType.GetName(typeof(TRequest)), batcherName);
}
