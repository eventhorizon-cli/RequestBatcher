using BufferQueue;
using Microsoft.Extensions.Logging;

namespace RequestBatcher.Internal;

internal sealed class RequestBatchConsumer<TRequest>(
    RequestBatchHandler<TRequest> handler,
    int batchSize,
    ILogger logger,
    string requestTypeName)
{
    public async Task RunAsync(
        IBufferPullConsumer<PendingBatchRequest<TRequest>> consumer,
        CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var bufferedRequests in consumer
                               .ConsumeAsync(cancellationToken)
                               .ConfigureAwait(false))
            {
                await ProcessAsync(bufferedRequests, cancellationToken).ConfigureAwait(false);
                await consumer.CommitAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.ConsumerFailed(exception, requestTypeName);
            throw;
        }
    }

    private async ValueTask ProcessAsync(
        IEnumerable<PendingBatchRequest<TRequest>> bufferedRequests,
        CancellationToken cancellationToken)
    {
        List<PendingBatchRequest<TRequest>>? activeRequests = null;

        try
        {
            foreach (var pendingRequest in bufferedRequests)
            {
                if (pendingRequest.TryStartProcessing())
                {
                    activeRequests ??= new List<PendingBatchRequest<TRequest>>(batchSize);
                    activeRequests.Add(pendingRequest);
                }
                else
                {
                    pendingRequest.FinishCanceledRequest();
                }
            }
        }
        catch (Exception exception)
        {
            CompleteWithError(activeRequests, exception);
            throw;
        }

        if (activeRequests is null)
        {
            return;
        }

        var requests = new TRequest[activeRequests.Count];
        for (var i = 0; i < activeRequests.Count; i++)
        {
            requests[i] = activeRequests[i].Request;
        }

        try
        {
            logger.BatchStarted(requestTypeName, requests.Length);
            await handler(requests, cancellationToken).ConfigureAwait(false);

            foreach (var pendingRequest in activeRequests)
            {
                pendingRequest.CompleteSuccessfully();
            }

            logger.BatchCompleted(requestTypeName, requests.Length);
        }
        catch (Exception exception)
        {
            foreach (var pendingRequest in activeRequests)
            {
                pendingRequest.CompleteWithError(exception);
            }

            logger.BatchFailed(
                exception,
                requestTypeName,
                requests.Length);
        }
    }

    private static void CompleteWithError(
        IEnumerable<PendingBatchRequest<TRequest>>? pendingRequests,
        Exception exception)
    {
        if (pendingRequests is null)
        {
            return;
        }

        foreach (var pendingRequest in pendingRequests)
        {
            pendingRequest.CompleteWithError(exception);
        }
    }
}
