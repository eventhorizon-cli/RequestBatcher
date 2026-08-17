using System.Runtime.ExceptionServices;
using BufferQueue;
using Microsoft.Extensions.Logging;
using RequestBatcher.Diagnostics;
using RequestBatcher.PendingRequests;

namespace RequestBatcher.Scheduling;

internal sealed class BatchDispatchLoop<TRequest> : IDisposable
{
    private readonly RequestBatchConsumer<TRequest> _batchConsumer;
    private readonly CancellationTokenSource _executionFailureCancellation = new();
    private readonly SemaphoreSlim _executionSlots;
    private readonly object _inFlightLock = new();
    private readonly ILogger _logger;
    private readonly string _requestTypeName;

    private TaskCompletionSource _drained = CreateCompletedSource();
    private Exception? _executionFailure;
    private int _inFlightCount;
    private int _disposed;

    public BatchDispatchLoop(
        RequestBatchHandler<TRequest> handler,
        int maxConcurrency,
        ILogger logger,
        string requestTypeName)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConcurrency, 1);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestTypeName);

        _batchConsumer = new RequestBatchConsumer<TRequest>(handler, logger, requestTypeName);
        _executionSlots = new SemaphoreSlim(maxConcurrency, maxConcurrency);
        _logger = logger;
        _requestTypeName = requestTypeName;
    }

    public Task DrainAsync()
    {
        lock (_inFlightLock)
        {
            return _drained.Task;
        }
    }

    public async Task RunAsync(
        IBufferPullConsumer<PendingBatchRequest<TRequest>> consumer,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(consumer);

        using var dispatchCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _executionFailureCancellation.Token);

        try
        {
            await using var enumerator = consumer
                .ConsumeAsync(dispatchCancellation.Token)
                .GetAsyncEnumerator(dispatchCancellation.Token);

            while (true)
            {
                await _executionSlots.WaitAsync(dispatchCancellation.Token).ConfigureAwait(false);
                var executionStarted = false;
                try
                {
                    if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                    {
                        return;
                    }

                    var pendingRequests = GetPendingRequests(enumerator.Current);
                    StartExecution(pendingRequests, cancellationToken);
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
            _logger.ConsumerFailed(executionFailure, _requestTypeName);
            ExceptionDispatchInfo.Capture(executionFailure).Throw();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logger.ConsumerFailed(exception, _requestTypeName);
            throw;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _executionFailureCancellation.Dispose();
        _executionSlots.Dispose();
    }

    private void StartExecution(
        IReadOnlyList<PendingBatchRequest<TRequest>> pendingRequests,
        CancellationToken cancellationToken)
    {
        TrackExecution();
        _ = ProcessBatchAsync(pendingRequests, cancellationToken);
    }

    private async Task ProcessBatchAsync(
        IReadOnlyList<PendingBatchRequest<TRequest>> pendingRequests,
        CancellationToken cancellationToken)
    {
        try
        {
            await _batchConsumer.ProcessAsync(pendingRequests, cancellationToken).ConfigureAwait(false);
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
            CompleteExecution();
            _executionSlots.Release();
        }
    }

    private Exception? GetExecutionFailure() => Volatile.Read(ref _executionFailure);

    private static IReadOnlyList<PendingBatchRequest<TRequest>> GetPendingRequests(
        IEnumerable<PendingBatchRequest<TRequest>> bufferedRequests) =>
        bufferedRequests as IReadOnlyList<PendingBatchRequest<TRequest>> ?? bufferedRequests.ToArray();

    private void TrackExecution()
    {
        lock (_inFlightLock)
        {
            if (_inFlightCount == 0)
            {
                _drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            _inFlightCount++;
        }
    }

    private void CompleteExecution()
    {
        TaskCompletionSource? drained = null;
        lock (_inFlightLock)
        {
            _inFlightCount--;
            if (_inFlightCount == 0)
            {
                drained = _drained;
            }
        }

        drained?.TrySetResult();
    }

    private static TaskCompletionSource CreateCompletedSource()
    {
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.SetResult();
        return source;
    }
}
