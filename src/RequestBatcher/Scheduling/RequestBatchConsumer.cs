using Microsoft.Extensions.Logging;
using RequestBatcher.Diagnostics;
using RequestBatcher.PendingRequests;

namespace RequestBatcher.Scheduling;

internal sealed class RequestBatchConsumer<TRequest>(
    RequestBatchHandler<TRequest> handler,
    ILogger logger,
    string requestTypeName)
{
    internal async ValueTask ProcessAsync(
        IReadOnlyList<PendingBatchRequest<TRequest>> bufferedRequests,
        CancellationToken cancellationToken)
    {
        List<PendingBatchRequest<TRequest>>? activeRequests = null;

        try
        {
            foreach (var pendingRequest in bufferedRequests)
            {
                if (pendingRequest.TryStartProcessing())
                {
                    activeRequests ??= new List<PendingBatchRequest<TRequest>>(bufferedRequests.Count);
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
