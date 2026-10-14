using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using BufferQueue;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RequestBatcher.Diagnostics;
using RequestBatcher.PendingRequests;
using RequestBatcher.Scheduling;

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
    private readonly ConcurrentDictionary<PendingBatchRequest<TRequest>, byte> _pendingRequests = new();
    private readonly Action<PendingBatchRequest<TRequest>> _onRequestFinished;
    private readonly PendingRequestProducer<TRequest> _producer;
    private readonly CancellationTokenSource _producerCancellation = new();
    private readonly CancellationTokenSource _consumerCancellation = new();
    private readonly BatchDispatchLoop<TRequest> _batchDispatchLoop;
    private readonly Task _dispatchLoopTask;
    private readonly ConsumerTaskMonitor _dispatchLoopMonitor;

    private Task? _disposeTask;
    private Exception? _consumerFailure;
    private TaskCompletionSource _drained = CreateCompletedSource();
    private int _pendingRequestCount;
    private int _state;

    internal RequestBatchCoordinator(
        IBufferQueue bufferQueue,
        RequestBatchHandler<TRequest> handler,
        IOptions<RequestBatchOptions<TRequest>> options,
        ILogger<RequestBatchCoordinator<TRequest>> logger)
    {
        _logger = logger;
        _options = options.Value.ValidateAndClone();
        _onRequestFinished = OnRequestFinished;

        _producer = new PendingRequestProducer<TRequest>(
            bufferQueue.GetProducer<PendingBatchRequest<TRequest>>(TopicName),
            _logger,
            _requestTypeName,
            _options.MaxPendingRequests,
            _producerCancellation.Token);

        _batchDispatchLoop = new BatchDispatchLoop<TRequest>(
            handler,
            _options.MaxConcurrency,
            _logger,
            _requestTypeName);
        var consumer = bufferQueue.CreatePullConsumers<PendingBatchRequest<TRequest>>(
            new BufferPullConsumerOptions
            {
                TopicName = TopicName,
                GroupName = ConsumerGroupName,
                BatchSize = _options.BatchSize,
                AutoCommit = true,
            },
            1)
            .Single();

        _dispatchLoopTask = _batchDispatchLoop.RunAsync(consumer, _consumerCancellation.Token);

        _logger.CoordinatorStarted(
            _requestTypeName,
            _options.BatchSize,
            _options.MaxConcurrency,
            _options.MaxPendingRequests,
            _options.FullMode);

        _dispatchLoopMonitor = new ConsumerTaskMonitor(
            [_dispatchLoopTask],
            _consumerCancellation.Token,
            _requestTypeName,
            HandleConsumerFailure);
    }

    /// <inheritdoc />
    public Task ExecuteAsync(TRequest request, CancellationToken cancellationToken = default) =>
        ExecuteAsync(request, null, cancellationToken);

    internal Task ExecuteAsync(
        TRequest request,
        IRequestCompletion? completion,
        CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _state) != Running)
        {
            return Task.FromException(CreateStoppedException());
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled(cancellationToken);
        }

        return AcceptRequest(request, completion, cancellationToken);
    }

    /// <inheritdoc />
    public Task ExecuteAsync(
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

        return AcceptBatch(requestArray, cancellationToken);
    }

    /// <summary>
    /// Stops accepting requests, drains accepted work, and releases coordinator-owned resources.
    /// Concurrent and repeated calls share the same disposal operation.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        lock (_lifecycleLock)
        {
            if (_disposeTask is null)
            {
                _state = Stopping;
                _disposeTask = DisposeCoreAsync(_drained.Task);
            }

            return new ValueTask(_disposeTask);
        }
    }

    private Task AcceptRequest(
        TRequest request,
        IRequestCompletion? completion,
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
                _onRequestFinished,
                completion);
            _pendingRequests.TryAdd(pendingRequest, 0);
        }

        return _producer.ProduceAsync(pendingRequest, cancellationToken);
    }

    private Task AcceptBatch(
        TRequest[] requests,
        CancellationToken cancellationToken)
    {
        PendingBatchRequest<TRequest>[] pendingRequests;
        BatchSubmissionCompletion batchCompletion;
        lock (_lifecycleLock)
        {
            if (_state != Running)
            {
                return Task.FromException(CreateStoppedException());
            }

            TrackRequestsLocked(requests.Length);
            batchCompletion = new BatchSubmissionCompletion(requests.Length, cancellationToken);
            pendingRequests = new PendingBatchRequest<TRequest>[requests.Length];
            for (var i = 0; i < requests.Length; i++)
            {
                pendingRequests[i] = new PendingBatchRequest<TRequest>(
                    requests[i],
                    cancellationToken,
                    _onRequestFinished,
                    batchCompletion);
                _pendingRequests.TryAdd(pendingRequests[i], 0);
            }
        }

        return _producer.ProduceAsync(pendingRequests, cancellationToken);
    }

    private void OnRequestFinished(PendingBatchRequest<TRequest> pendingRequest)
    {
        if (!_pendingRequests.TryRemove(pendingRequest, out _))
        {
            return;
        }

        if (Interlocked.Decrement(ref _pendingRequestCount) != 0)
        {
            return;
        }

        TaskCompletionSource? drained = null;
        lock (_lifecycleLock)
        {
            if (Volatile.Read(ref _pendingRequestCount) == 0)
            {
                drained = _drained;
            }
        }

        drained?.TrySetResult();
    }

    private void TrackRequestsLocked(int requestCount)
    {
        if (Volatile.Read(ref _pendingRequestCount) == 0)
        {
            _drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        Interlocked.Add(ref _pendingRequestCount, requestCount);
    }

    private async Task DisposeCoreAsync(Task pendingRequestsDrained)
    {
        await Task.Yield();
        _logger.CoordinatorStopping(
            _requestTypeName,
            Volatile.Read(ref _pendingRequestCount));
        try
        {
            await pendingRequestsDrained.ConfigureAwait(false);
            await _batchDispatchLoop.DrainAsync().ConfigureAwait(false);
            await _consumerCancellation.CancelAsync().ConfigureAwait(false);
            await _dispatchLoopTask.ConfigureAwait(false);

            if (_consumerFailure is not null)
            {
                ExceptionDispatchInfo.Capture(_consumerFailure).Throw();
            }
        }
        finally
        {
            try
            {
                await _dispatchLoopMonitor.Completion.ConfigureAwait(false);
                _batchDispatchLoop.Dispose();
                _producerCancellation.Dispose();
                _consumerCancellation.Dispose();
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
            _state = Stopping;

            pendingRequests = _pendingRequests.Keys.ToArray();
        }

        foreach (var pendingRequest in pendingRequests)
        {
            pendingRequest.FailWhileQueued(exception);
        }

        // No consumer can reliably release capacity after a consumer loop fails.
        _producerCancellation.Cancel();
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
