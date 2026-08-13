using Microsoft.Extensions.Logging;

namespace RequestBatcher;

internal static partial class RequestBatcherLog
{
    [LoggerMessage(
        EventId = 1000,
        Level = LogLevel.Debug,
        Message = "Request batcher for {RequestType} started. BatchSize={BatchSize}, MaxConcurrency={MaxConcurrency}, " +
                  "MaxPendingRequests={MaxPendingRequests}, FullMode={FullMode}.")]
    public static partial void CoordinatorStarted(
        ILogger logger,
        string requestType,
        int batchSize,
        int maxConcurrency,
        int maxPendingRequests,
        RequestBatchFullMode fullMode);

    [LoggerMessage(
        EventId = 1001,
        Level = LogLevel.Warning,
        Message = "Request batcher queue for {RequestType} is full; rejecting request at capacity " +
                  "{MaxPendingRequests}.")]
    public static partial void QueueFull(
        ILogger logger,
        string requestType,
        int maxPendingRequests);

    [LoggerMessage(
        EventId = 1002,
        Level = LogLevel.Error,
        Message = "Failed to enqueue a {RequestType} request.")]
    public static partial void EnqueueFailed(
        ILogger logger,
        Exception exception,
        string requestType);

    [LoggerMessage(
        EventId = 1003,
        Level = LogLevel.Trace,
        Message = "Processing a {RequestType} batch containing {BatchSize} requests.")]
    public static partial void BatchStarted(
        ILogger logger,
        string requestType,
        int batchSize);

    [LoggerMessage(
        EventId = 1004,
        Level = LogLevel.Trace,
        Message = "Processed a {RequestType} batch containing {BatchSize} requests.")]
    public static partial void BatchCompleted(
        ILogger logger,
        string requestType,
        int batchSize);

    [LoggerMessage(
        EventId = 1005,
        Level = LogLevel.Error,
        Message = "Failed to process a {RequestType} batch containing {BatchSize} requests.")]
    public static partial void BatchFailed(
        ILogger logger,
        Exception exception,
        string requestType,
        int batchSize);

    [LoggerMessage(
        EventId = 1006,
        Level = LogLevel.Critical,
        Message = "A request batcher consumer for {RequestType} stopped unexpectedly.")]
    public static partial void ConsumerFailed(
        ILogger logger,
        Exception exception,
        string requestType);

    [LoggerMessage(
        EventId = 1007,
        Level = LogLevel.Debug,
        Message = "Stopping request batcher for {RequestType}; draining {PendingRequestCount} accepted requests.")]
    public static partial void CoordinatorStopping(
        ILogger logger,
        string requestType,
        int pendingRequestCount);

    [LoggerMessage(
        EventId = 1008,
        Level = LogLevel.Debug,
        Message = "Request batcher for {RequestType} stopped.")]
    public static partial void CoordinatorStopped(
        ILogger logger,
        string requestType);
}
