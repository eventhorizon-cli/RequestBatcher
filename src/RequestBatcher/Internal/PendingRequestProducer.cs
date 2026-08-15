using BufferQueue;
using Microsoft.Extensions.Logging;

namespace RequestBatcher.Internal;

internal sealed class PendingRequestProducer<TRequest>(
    IBufferProducer<PendingBatchRequest<TRequest>> producer,
    ILogger logger,
    string requestTypeName,
    int capacity,
    CancellationToken stoppingToken)
{
    public Task ProduceAsync(
        PendingBatchRequest<TRequest> pendingRequest,
        CancellationToken cancellationToken)
    {
        var linkedCancellation = CreateProducerCancellation(cancellationToken, out var producerCancellation);
        try
        {
            var produceTask = producer.ProduceAsync(pendingRequest, producerCancellation);
            if (produceTask.IsCompletedSuccessfully)
            {
                linkedCancellation?.Dispose();
                return pendingRequest.Completion;
            }

            return AwaitProduceAndCompletionAsync(
                produceTask,
                pendingRequest,
                linkedCancellation,
                cancellationToken);
        }
        catch (Exception exception)
        {
            linkedCancellation?.Dispose();
            FailWhileQueued(pendingRequest, exception, 1, cancellationToken);
            return pendingRequest.Completion;
        }
    }

    public Task ProduceAsync(
        PendingBatchRequest<TRequest>[] pendingRequests,
        CancellationToken cancellationToken)
    {
        var completions = new Task[pendingRequests.Length];
        for (var i = 0; i < pendingRequests.Length; i++)
        {
            completions[i] = pendingRequests[i].Completion;
        }

        var completion = Task.WhenAll(completions);
        var linkedCancellation = CreateProducerCancellation(cancellationToken, out var producerCancellation);
        try
        {
            var produceTask = producer.ProduceAsync(pendingRequests.AsMemory(), producerCancellation);
            if (produceTask.IsCompletedSuccessfully)
            {
                linkedCancellation?.Dispose();
                return completion;
            }

            return AwaitProduceAndCompletionAsync(
                produceTask,
                pendingRequests,
                completion,
                linkedCancellation,
                cancellationToken);
        }
        catch (Exception exception)
        {
            linkedCancellation?.Dispose();
            FailWhileQueued(
                pendingRequests,
                exception,
                pendingRequests.Length,
                cancellationToken);
            return completion;
        }
    }

    private async Task AwaitProduceAndCompletionAsync(
        ValueTask produceTask,
        PendingBatchRequest<TRequest> pendingRequest,
        CancellationTokenSource? linkedCancellation,
        CancellationToken cancellationToken)
    {
        try
        {
            await produceTask.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            FailWhileQueued(pendingRequest, exception, 1, cancellationToken);
        }
        finally
        {
            linkedCancellation?.Dispose();
        }

        await pendingRequest.Completion.ConfigureAwait(false);
    }

    private async Task AwaitProduceAndCompletionAsync(
        ValueTask produceTask,
        PendingBatchRequest<TRequest>[] pendingRequests,
        Task completion,
        CancellationTokenSource? linkedCancellation,
        CancellationToken cancellationToken)
    {
        try
        {
            await produceTask.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            FailWhileQueued(
                pendingRequests,
                exception,
                pendingRequests.Length,
                cancellationToken);
        }
        finally
        {
            linkedCancellation?.Dispose();
        }

        await completion.ConfigureAwait(false);
    }

    private void FailWhileQueued(
        PendingBatchRequest<TRequest> pendingRequest,
        Exception exception,
        int requestCount,
        CancellationToken cancellationToken)
    {
        var mappedException = MapException(exception, requestCount, cancellationToken);
        pendingRequest.FailWhileQueued(mappedException);
        LogUnexpectedEnqueueFailure(exception, requestCount, cancellationToken);
    }

    private void FailWhileQueued(
        IEnumerable<PendingBatchRequest<TRequest>> pendingRequests,
        Exception exception,
        int requestCount,
        CancellationToken cancellationToken)
    {
        var mappedException = MapException(exception, requestCount, cancellationToken);
        foreach (var pendingRequest in pendingRequests)
        {
            pendingRequest.FailWhileQueued(mappedException);
        }

        LogUnexpectedEnqueueFailure(exception, requestCount, cancellationToken);
    }

    private Exception MapException(
        Exception exception,
        int requestCount,
        CancellationToken cancellationToken)
    {
        if (exception is BufferQueueFullException)
        {
            logger.QueueFull(requestTypeName, requestCount, capacity);
            return new RequestBatchQueueFullException(capacity, requestCount);
        }

        if (exception is OperationCanceledException &&
            stoppingToken.IsCancellationRequested &&
            !cancellationToken.IsCancellationRequested)
        {
            return new ObjectDisposedException(typeof(RequestBatchCoordinator<TRequest>).FullName);
        }

        return exception;
    }

    private void LogUnexpectedEnqueueFailure(
        Exception exception,
        int requestCount,
        CancellationToken cancellationToken)
    {
        if (exception is BufferQueueFullException ||
            exception is OperationCanceledException &&
            (stoppingToken.IsCancellationRequested || cancellationToken.IsCancellationRequested))
        {
            return;
        }

        logger.EnqueueFailed(exception, requestTypeName, requestCount);
    }

    private CancellationTokenSource? CreateProducerCancellation(
        CancellationToken cancellationToken,
        out CancellationToken producerCancellation)
    {
        if (!cancellationToken.CanBeCanceled)
        {
            producerCancellation = stoppingToken;
            return null;
        }

        var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            stoppingToken);
        producerCancellation = linkedCancellation.Token;
        return linkedCancellation;
    }
}
