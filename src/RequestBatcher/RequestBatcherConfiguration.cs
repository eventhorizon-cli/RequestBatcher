
using RequestBatcher.Diagnostics;

namespace RequestBatcher;

internal sealed class RequestBatcherConfiguration<TRequest>(
    string? batcherName = null,
    RequestBatchOptions<TRequest>? options = null)
{
    // Each registration owns a topic; request types and public names must not determine queue isolation.
    // Generate it once so queue registration, the producer, and the consumer all use the same identity.
    public string TopicName { get; } = $"request-batcher-{Guid.NewGuid():N}";
    public RequestBatchOptions<TRequest>? Options { get; } = options;
    public RequestBatcherMetadata Metadata { get; } = new(
        RequestBatcherRequestType.GetName(typeof(TRequest)), batcherName);
}
