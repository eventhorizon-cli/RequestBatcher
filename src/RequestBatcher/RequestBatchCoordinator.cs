using BufferQueue;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace RequestBatcher;

/// <summary>
/// Transparently coalesces concurrent requests into batches and processes batches with bounded concurrency.
/// </summary>
/// <typeparam name="TRequest">The request type.</typeparam>
public sealed class RequestBatchCoordinator<TRequest> : IRequestBatcher<TRequest>, IAsyncDisposable
{
    internal const string TopicName = "request-batcher";
    internal const string ConsumerGroupName = "request-batcher-consumers";
    private const int Running = 0;
    private const int Stopping = 1;
    private const int Stopped = 2;
    private static readonly string _requestTypeName =
        typeof(TRequest).FullName ?? typeof(TRequest).Name;

    private readonly object _lifecycleLock = new();
    private readonly RequestBatchHandler<TRequest> _handler;
    private readonly ILogger<RequestBatchCoordinator<TRequest>> _logger;
    private readonly RequestBatchOptions<TRequest> _options;
    private readonly SemaphoreSlim _capacityGate;
    private readonly CancellationTokenSource _admissionCancellation = new();
    private readonly CancellationTokenSource _consumerCancellation = new();
    private readonly IBufferProducer<PendingBatchRequest<TRequest>> _producer;
    private readonly Task[] _consumerTasks;

    private TaskCompletionSource _drained = CreateCompletedSource();
    private Task? _stopTask;
    private Task? _disposeTask;
    private int _pendingRequestCount;
    private int _state;

    internal RequestBatchCoordinator(
        IBufferQueue bufferQueue,
        IRequestBatchHandler<TRequest> handler,
        RequestBatchOptions<TRequest>? options = null,
        ILogger<RequestBatchCoordinator<TRequest>>? logger = null)
        : this(
            bufferQueue,
            handler is null ? throw new ArgumentNullException(nameof(handler)) : handler.HandleAsync,
            options,
            logger)
    {
    }

    internal RequestBatchCoordinator(
        IBufferQueue bufferQueue,
        RequestBatchHandler<TRequest> handler,
        RequestBatchOptions<TRequest>? options = null,
        ILogger<RequestBatchCoordinator<TRequest>>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(bufferQueue);

        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        _logger = logger ?? NullLogger<RequestBatchCoordinator<TRequest>>.Instance;
        _options = (options ?? new RequestBatchOptions<TRequest>()).ValidateAndClone();
        _capacityGate = new SemaphoreSlim(_options.MaxPendingRequests, _options.MaxPendingRequests);

        _producer = bufferQueue.GetProducer<PendingBatchRequest<TRequest>>(TopicName);

        var consumers = bufferQueue.CreatePullConsumers<PendingBatchRequest<TRequest>>(
            new BufferPullConsumerOptions
            {
                TopicName = TopicName,
                GroupName = ConsumerGroupName,
                BatchSize = _options.BatchSize,
                AutoCommit = true,
            },
            _options.MaxConcurrency);

        _consumerTasks = consumers
            .Select(consumer => RunConsumerAsync(consumer, _consumerCancellation.Token))
            .ToArray();

        RequestBatcherLog.CoordinatorStarted(
            _logger,
            _requestTypeName,
            _options.BatchSize,
            _options.MaxConcurrency,
            _options.MaxPendingRequests,
            _options.FullMode);
    }

    /// <inheritdoc />
    public Task ProcessAsync(TRequest request, CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _state) != Running)
        {
            return Task.FromException(CreateStoppedException());
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled(cancellationToken);
        }

        if (_capacityGate.Wait(0))
        {
            return EnqueueWithReservedCapacity(request, cancellationToken);
        }

        if (_options.FullMode == RequestBatchFullMode.Fail)
        {
            if (Volatile.Read(ref _state) != Running)
            {
                return Task.FromException(CreateStoppedException());
            }

            RequestBatcherLog.QueueFull(_logger, _requestTypeName, _options.MaxPendingRequests);
            return Task.FromException(new RequestBatchQueueFullException(_options.MaxPendingRequests));
        }

        return WaitForCapacityAndEnqueueAsync(request, cancellationToken);
    }

    /// <summary>
    /// Stops accepting requests, drains all accepted requests, and then stops the consumers.
    /// Cancellation only cancels this wait; shutdown continues in the background.
    /// </summary>
    public ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        Task stopTask;
        lock (_lifecycleLock)
        {
            stopTask = GetOrStartStopTaskLocked();
        }

        return cancellationToken.CanBeCanceled
            ? new ValueTask(stopTask.WaitAsync(cancellationToken))
            : new ValueTask(stopTask);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        Task disposeTask;
        lock (_lifecycleLock)
        {
            var stopTask = GetOrStartStopTaskLocked();
            disposeTask = _disposeTask ??= DisposeCoreAsync(stopTask);
        }

        return new ValueTask(disposeTask);
    }

    private async Task WaitForCapacityAndEnqueueAsync(
        TRequest request,
        CancellationToken cancellationToken)
    {
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _admissionCancellation.Token);

        try
        {
            await _capacityGate.WaitAsync(linkedCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            _admissionCancellation.IsCancellationRequested &&
            !cancellationToken.IsCancellationRequested)
        {
            throw CreateStoppedException();
        }

        if (cancellationToken.IsCancellationRequested)
        {
            _capacityGate.Release();
            cancellationToken.ThrowIfCancellationRequested();
        }

        await EnqueueWithReservedCapacity(request, cancellationToken).ConfigureAwait(false);
    }

    private Task EnqueueWithReservedCapacity(
        TRequest request,
        CancellationToken cancellationToken)
    {
        PendingBatchRequest<TRequest> pendingRequest;
        lock (_lifecycleLock)
        {
            if (_state != Running)
            {
                _capacityGate.Release();
                return Task.FromException(CreateStoppedException());
            }

            if (_pendingRequestCount++ == 0)
            {
                _drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            pendingRequest = new PendingBatchRequest<TRequest>(
                request,
                cancellationToken,
                OnRequestFinished);
        }

        try
        {
            var produceTask = _producer.ProduceAsync(pendingRequest);
            if (produceTask.IsCompletedSuccessfully)
            {
                return pendingRequest.Completion;
            }

            return AwaitProduceAndCompletionAsync(produceTask, pendingRequest);
        }
        catch (Exception exception)
        {
            pendingRequest.FailBeforeEnqueue(exception);
            RequestBatcherLog.EnqueueFailed(_logger, exception, _requestTypeName);
            return pendingRequest.Completion;
        }
    }

    private async Task AwaitProduceAndCompletionAsync(
        ValueTask produceTask,
        PendingBatchRequest<TRequest> pendingRequest)
    {
        try
        {
            await produceTask.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            pendingRequest.FailBeforeEnqueue(exception);
            RequestBatcherLog.EnqueueFailed(_logger, exception, _requestTypeName);
        }

        await pendingRequest.Completion.ConfigureAwait(false);
    }

    private async Task RunConsumerAsync(
        IBufferPullConsumer<PendingBatchRequest<TRequest>> consumer,
        CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var bufferedRequests in consumer
                               .ConsumeAsync(cancellationToken)
                               .ConfigureAwait(false))
            {
                await ProcessBatchAsync(bufferedRequests, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            RequestBatcherLog.ConsumerFailed(_logger, exception, _requestTypeName);
            throw;
        }
    }

    private async ValueTask ProcessBatchAsync(
        IEnumerable<PendingBatchRequest<TRequest>> bufferedRequests,
        CancellationToken cancellationToken)
    {
        List<PendingBatchRequest<TRequest>>? activeRequests = null;

        foreach (var pendingRequest in bufferedRequests)
        {
            if (pendingRequest.TryStartProcessing())
            {
                activeRequests ??= new List<PendingBatchRequest<TRequest>>(_options.BatchSize);
                activeRequests.Add(pendingRequest);
            }
            else
            {
                pendingRequest.FinishCanceledRequest();
            }
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
            RequestBatcherLog.BatchStarted(_logger, _requestTypeName, requests.Length);
            await _handler(requests, cancellationToken).ConfigureAwait(false);

            foreach (var pendingRequest in activeRequests)
            {
                pendingRequest.CompleteSuccessfully();
            }

            RequestBatcherLog.BatchCompleted(_logger, _requestTypeName, requests.Length);
        }
        catch (Exception exception)
        {
            foreach (var pendingRequest in activeRequests)
            {
                pendingRequest.CompleteWithError(exception);
            }

            RequestBatcherLog.BatchFailed(_logger, exception, _requestTypeName, requests.Length);
        }
    }

    private void OnRequestFinished(PendingBatchRequest<TRequest> _)
    {
        _capacityGate.Release();

        TaskCompletionSource? drained = null;
        lock (_lifecycleLock)
        {
            _pendingRequestCount--;
            if (_pendingRequestCount == 0)
            {
                drained = _drained;
            }
        }

        drained?.TrySetResult();
    }

    private Task GetOrStartStopTaskLocked()
    {
        if (_stopTask is not null)
        {
            return _stopTask;
        }

        _state = Stopping;
        _admissionCancellation.Cancel();
        _stopTask = StopCoreAsync(_drained.Task);
        return _stopTask;
    }

    private async Task StopCoreAsync(Task drainedTask)
    {
        await Task.Yield();
        RequestBatcherLog.CoordinatorStopping(
            _logger,
            _requestTypeName,
            Volatile.Read(ref _pendingRequestCount));
        await drainedTask.ConfigureAwait(false);
        await _consumerCancellation.CancelAsync().ConfigureAwait(false);
        await Task.WhenAll(_consumerTasks).ConfigureAwait(false);

        lock (_lifecycleLock)
        {
            _state = Stopped;
        }

        RequestBatcherLog.CoordinatorStopped(_logger, _requestTypeName);
    }

    private async Task DisposeCoreAsync(Task stopTask)
    {
        await stopTask.ConfigureAwait(false);
        _admissionCancellation.Dispose();
        _consumerCancellation.Dispose();
        _capacityGate.Dispose();
    }

    private static TaskCompletionSource CreateCompletedSource()
    {
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.SetResult();
        return source;
    }

    private static ObjectDisposedException CreateStoppedException() =>
        new(typeof(RequestBatchCoordinator<TRequest>).FullName);
}
