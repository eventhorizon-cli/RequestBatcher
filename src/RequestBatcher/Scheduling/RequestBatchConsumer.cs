using Microsoft.Extensions.Logging;
using RequestBatcher.Diagnostics;
using RequestBatcher.PendingRequests;

namespace RequestBatcher.Scheduling;

internal sealed class RequestBatchConsumer<TRequest>(
    RequestBatchHandler<TRequest> handler,
    ILogger logger,
    string requestTypeName)
{
    internal async ValueTask ExecuteAsync(
        IReadOnlyList<PendingBatchRequest<TRequest>> bufferedRequests,
        CancellationToken cancellationToken)
    {
        var activeRequests = StartQueuedRequests(bufferedRequests);
        if (activeRequests.Count == 0)
        {
            return;
        }

        var requests = CreateHandlerInput(activeRequests);

        try
        {
            logger.BatchStarted(requestTypeName, requests.Length);
            await handler(requests, cancellationToken).ConfigureAwait(false);

            for (var i = 0; i < activeRequests.Count; i++)
            {
                activeRequests[i].CompleteSuccessfully();
            }

            logger.BatchCompleted(requestTypeName, requests.Length);
        }
        catch (Exception exception)
        {
            CompleteWithError(activeRequests, exception);

            logger.BatchFailed(
                exception,
                requestTypeName,
                requests.Length);
        }
    }

    private static IReadOnlyList<PendingBatchRequest<TRequest>> StartQueuedRequests(
        IReadOnlyList<PendingBatchRequest<TRequest>> bufferedRequests)
    {
        // BufferQueue gives each batch an independent snapshot that remains valid across the handler await.
        // Reuse it unchanged when every request starts; otherwise return a list containing only started requests.
        List<PendingBatchRequest<TRequest>>? filteredRequests = null;
        var startedCount = 0;

        try
        {
            foreach (var pendingRequest in bufferedRequests)
            {
                if (!pendingRequest.TryStartProcessing())
                {
                    // Copy the started prefix once, then omit every skipped request, including trailing ones.
                    filteredRequests ??= CopyStartedPrefix(bufferedRequests, startedCount);
                    pendingRequest.FinishCanceledRequest();
                    continue;
                }

                filteredRequests?.Add(pendingRequest);
                startedCount++;
            }
        }
        catch (Exception exception)
        {
            // Selection failed partway through: only requests already started receive this failure.
            var startedRequests = filteredRequests ?? bufferedRequests;
            for (var i = 0; i < startedCount; i++)
            {
                startedRequests[i].CompleteWithError(exception);
            }

            throw;
        }

        return filteredRequests ?? bufferedRequests;
    }

    private static List<PendingBatchRequest<TRequest>> CopyStartedPrefix(
        IReadOnlyList<PendingBatchRequest<TRequest>> bufferedRequests,
        int startedCount)
    {
        // Before the first gap, all started requests occupy the original snapshot's prefix.
        var requests = new List<PendingBatchRequest<TRequest>>(bufferedRequests.Count);
        for (var i = 0; i < startedCount; i++)
        {
            requests.Add(bufferedRequests[i]);
        }

        return requests;
    }

    private static TRequest[] CreateHandlerInput(
        IReadOnlyList<PendingBatchRequest<TRequest>> activeRequests)
    {
        // Keep handler input independent so retaining it cannot expose internal pending-request state.
        var requests = new TRequest[activeRequests.Count];
        for (var i = 0; i < activeRequests.Count; i++)
        {
            requests[i] = activeRequests[i].Request;
        }

        return requests;
    }

    private static void CompleteWithError(
        IReadOnlyList<PendingBatchRequest<TRequest>> pendingRequests,
        Exception exception)
    {
        for (var i = 0; i < pendingRequests.Count; i++)
        {
            pendingRequests[i].CompleteWithError(exception);
        }
    }
}
