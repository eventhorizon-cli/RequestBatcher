using System.Runtime.ExceptionServices;
using BufferQueue;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RequestBatcher.Internal;

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
    private readonly ILogger<RequestBatchCoordinator<TRequest>> _logger;
    private readonly RequestBatchOptions<TRequest> _options;
    private readonly HashSet<PendingBatchRequest<TRequest>> _pendingRequests = [];
    private readonly PendingRequestProducer<TRequest> _producer;
    private readonly CancellationTokenSource _producerCancellation = new();
    private readonly CancellationTokenSource _consumerCancellation = new();
    private readonly Task[] _consumerTasks;
    private readonly ConsumerTaskMonitor _consumerMonitor;

    private Task? _stopTask;
    private Task? _disposeTask;
    private Exception? _consumerFailure;
    private TaskCompletionSource _drained = CreateCompletedSource();
    private int _pendingRequestCount;
    private int _state;

    internal RequestBatchCoordinator(
        IBufferQueue bufferQueue,
        IRequestBatchHandler<TRequest> handler,
        IOptions<RequestBatchOptions<TRequest>> options,
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
        IOptions<RequestBatchOptions<TRequest>> options,
        ILogger<RequestBatchCoordinator<TRequest>>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(bufferQueue);
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(options);

        _logger = logger ?? NullLogger<RequestBatchCoordinator<TRequest>>.Instance;
        _options = options.Value.ValidateAndClone();

        _producer = new PendingRequestProducer<TRequest>(
            bufferQueue.GetProducer<PendingBatchRequest<TRequest>>(TopicName),
            _logger,
            _requestTypeName,
            _options.MaxPendingRequests,
            _producerCancellation.Token);

        var batchConsumer = new RequestBatchConsumer<TRequest>(
            handler,
            _options.BatchSize,
            _logger,
            _requestTypeName);
        var consumers = bufferQueue.CreatePullConsumers<PendingBatchRequest<TRequest>>(
            new BufferPullConsumerOptions
            {
                TopicName = TopicName,
                GroupName = ConsumerGroupName,
                BatchSize = _options.BatchSize,
                AutoCommit = false,
            },
            _options.MaxConcurrency);

        _consumerTasks = consumers
            .Select(consumer => batchConsumer.RunAsync(consumer, _consumerCancellation.Token))
            .ToArray();

        _logger.CoordinatorStarted(
            _requestTypeName,
            _options.BatchSize,
            _options.MaxConcurrency,
            _options.MaxPendingRequests,
            _options.FullMode);

        _consumerMonitor = new ConsumerTaskMonitor(
            _consumerTasks,
            _consumerCancellation.Token,
            _requestTypeName,
            HandleConsumerFailure);
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

        return AcceptRequest(request, cancellationToken);
    }

    /// <inheritdoc />
    public Task ProcessAsync(
        IEnumerable<TRequest> requests,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requests);

        if (Volatile.Read(ref _state) != Running)
        {
            return Task.FromException(CreateStoppedException());
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled(cancellationToken);
        }

        TRequest[] requestArray;
        try
        {
            requestArray = requests.ToArray();
        }
        catch (Exception exception)
        {
            return Task.FromException(exception);
        }

        if (Volatile.Read(ref _state) != Running)
        {
            return Task.FromException(CreateStoppedException());
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled(cancellationToken);
        }

        if (requestArray.Length == 0)
        {
            return Task.CompletedTask;
        }

        if (requestArray.Length > _options.MaxPendingRequests)
        {
            return CreateQueueFullTask(requestArray.Length);
        }

        return AcceptBatch(requestArray, cancellationToken);
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

    private Task AcceptRequest(
        TRequest request,
        CancellationToken cancellationToken)
    {
        PendingBatchRequest<TRequest> pendingRequest;
        lock (_lifecycleLock)
        {
            if (_state != Running)
            {
                return Task.FromException(CreateStoppedException());
            }

            TrackRequestsLocked(1);
            pendingRequest = new PendingBatchRequest<TRequest>(
                request,
                cancellationToken,
                OnRequestFinished);
            _pendingRequests.Add(pendingRequest);
        }

        return _producer.ProduceAsync(pendingRequest, cancellationToken);
    }

    private Task AcceptBatch(
        TRequest[] requests,
        CancellationToken cancellationToken)
    {
        PendingBatchRequest<TRequest>[] pendingRequests;
        lock (_lifecycleLock)
        {
            if (_state != Running)
            {
                return Task.FromException(CreateStoppedException());
            }

            TrackRequestsLocked(requests.Length);
            pendingRequests = new PendingBatchRequest<TRequest>[requests.Length];
            for (var i = 0; i < requests.Length; i++)
            {
                pendingRequests[i] = new PendingBatchRequest<TRequest>(
                    requests[i],
                    cancellationToken,
                    OnRequestFinished);
                _pendingRequests.Add(pendingRequests[i]);
            }
        }

        return _producer.ProduceAsync(pendingRequests, cancellationToken);
    }

    private void OnRequestFinished(PendingBatchRequest<TRequest> pendingRequest)
    {
        TaskCompletionSource? drained = null;
        lock (_lifecycleLock)
        {
            if (_pendingRequests.Remove(pendingRequest))
            {
                _pendingRequestCount--;
                if (_pendingRequestCount == 0)
                {
                    drained = _drained;
                }
            }
        }

        drained?.TrySetResult();
    }

    private void TrackRequestsLocked(int requestCount)
    {
        if (_pendingRequestCount == 0)
        {
            _drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        _pendingRequestCount += requestCount;
    }

    private Task CreateQueueFullTask(int requestCount)
    {
        _logger.QueueFull(
            _requestTypeName,
            requestCount,
            _options.MaxPendingRequests);
        return Task.FromException(
            new RequestBatchQueueFullException(_options.MaxPendingRequests, requestCount));
    }

    private Task GetOrStartStopTaskLocked()
    {
        if (_stopTask is not null)
        {
            return _stopTask;
        }

        _state = Stopping;
        _stopTask = StopCoreAsync(_drained.Task);
        return _stopTask;
    }

    private async Task StopCoreAsync(Task pendingRequestsDrained)
    {
        await Task.Yield();
        _logger.CoordinatorStopping(
            _requestTypeName,
            Volatile.Read(ref _pendingRequestCount));
        try
        {
            await _producerCancellation.CancelAsync().ConfigureAwait(false);
            await pendingRequestsDrained.ConfigureAwait(false);
            await _consumerCancellation.CancelAsync().ConfigureAwait(false);
            await Task.WhenAll(_consumerTasks).ConfigureAwait(false);

            if (_consumerFailure is not null)
            {
                ExceptionDispatchInfo.Capture(_consumerFailure).Throw();
            }
        }
        finally
        {
            lock (_lifecycleLock)
            {
                _state = Stopped;
            }

            _logger.CoordinatorStopped(_requestTypeName);
        }
    }

    private async Task DisposeCoreAsync(Task stopTask)
    {
        try
        {
            await stopTask.ConfigureAwait(false);
        }
        finally
        {
            await _consumerMonitor.Completion.ConfigureAwait(false);
            _producerCancellation.Dispose();
            _consumerCancellation.Dispose();
        }
    }

    private void HandleConsumerFailure(Exception exception)
    {
        PendingBatchRequest<TRequest>[] pendingRequests;
        lock (_lifecycleLock)
        {
            if (_state == Stopped)
            {
                return;
            }

            _consumerFailure ??= exception;
            if (_stopTask is null)
            {
                _state = Stopping;
                _stopTask = StopCoreAsync(_drained.Task);
            }

            pendingRequests = _pendingRequests.ToArray();
        }

        foreach (var pendingRequest in pendingRequests)
        {
            pendingRequest.FailWhileQueued(exception);
        }
    }

    private static ObjectDisposedException CreateStoppedException() =>
        new(typeof(RequestBatchCoordinator<TRequest>).FullName);

    private static TaskCompletionSource CreateCompletedSource()
    {
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.SetResult();
        return source;
    }
}
