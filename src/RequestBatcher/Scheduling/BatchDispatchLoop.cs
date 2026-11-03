using System.Runtime.ExceptionServices;
using BufferQueue;
using Microsoft.Extensions.Logging;
using RequestBatcher.Diagnostics;
using RequestBatcher.Diagnostics.Logging;
using RequestBatcher.Diagnostics.Metrics;
using RequestBatcher.Handlers;
using RequestBatcher.PendingRequests;

namespace RequestBatcher.Scheduling;

internal sealed class BatchDispatchLoop<TRequest> : IAsyncDisposable
{
    private readonly RequestBatchConsumer<TRequest> _batchConsumer;
    private readonly CancellationTokenSource _executionFailureCancellation = new();
    private readonly SemaphoreSlim _executionSlots;
    private readonly ILogger _logger;
    private readonly int _maxConcurrency;
    private readonly RequestBatcherMetrics _metrics;
    private readonly RequestBatcherMetadata _metadata;

    private Exception? _executionFailure;

    public BatchDispatchLoop(
        RequestBatchHandler<TRequest> handler,
        int maxConcurrency,
        ILogger logger,
        RequestBatcherMetadata metadata,
        RequestBatcherMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConcurrency, 1);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(metadata);

        _batchConsumer = new RequestBatchConsumer<TRequest>(handler, logger, metadata, metrics);
        _executionSlots = new SemaphoreSlim(maxConcurrency, maxConcurrency);
        _logger = logger;
        _maxConcurrency = maxConcurrency;
        _metrics = metrics;
        _metadata = metadata;
    }

    public async Task RunAsync(
        IBufferPullConsumer<PendingBatchRequest<TRequest>> consumer,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(consumer);

        using var dispatchCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _executionFailureCancellation.Token);
        var dispatchToken = dispatchCancellation.Token;
        using var backlogRegistration = _metrics.RegisterBacklog(
            consumer,
            _metadata.RequestTypeName,
            _metadata.BatcherName);

        try
        {
            await using var enumerator = consumer
                .ConsumeAsync(dispatchToken)
                .GetAsyncEnumerator(dispatchToken);

            while (true)
            {
                await _executionSlots.WaitAsync(dispatchToken).ConfigureAwait(false);
                var executionStarted = false;
                try
                {
                    if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                    {
                        return;
                    }

                    StartExecution(enumerator.Current, cancellationToken);
                    executionStarted = true;
                }
                finally
                {
                    if (!executionStarted)
                    {
                        _executionSlots.Release();
                    }
                }
            }
        }
        catch (OperationCanceledException) when (GetExecutionFailure() is { } executionFailure)
        {
            _logger.ConsumerFailed(executionFailure, _metadata.RequestTypeName);
            ExceptionDispatchInfo.Capture(executionFailure).Throw();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logger.ConsumerFailed(exception, _metadata.RequestTypeName);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        // The coordinator calls this once after awaiting RunAsync, so no new execution can acquire a permit.
        for (var i = 0; i < _maxConcurrency; i++)
        {
            await _executionSlots.WaitAsync().ConfigureAwait(false);
        }

        // Every execution has finished failure handling and returned its permit before resources are released.
        _executionFailureCancellation.Dispose();
        _executionSlots.Dispose();
    }

    private void StartExecution(
        IReadOnlyList<PendingBatchRequest<TRequest>> pendingRequests,
        CancellationToken cancellationToken)
    {
        var processing = _batchConsumer.ExecuteAsync(pendingRequests, cancellationToken);
        if (processing.IsCompletedSuccessfully)
        {
            _executionSlots.Release();
            return;
        }

        _ = ProcessBatchAsync(processing);
    }

    private async Task ProcessBatchAsync(ValueTask processing)
    {
        try
        {
            await processing.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            if (Interlocked.CompareExchange(ref _executionFailure, exception, null) is null)
            {
                _executionFailureCancellation.Cancel();
            }
        }
        finally
        {
            _executionSlots.Release();
        }
    }

    private Exception? GetExecutionFailure() => Volatile.Read(ref _executionFailure);
}
