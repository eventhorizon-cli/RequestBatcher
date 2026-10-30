using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using BufferQueue;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RequestBatcher.Diagnostics;
using RequestBatcher.Diagnostics.Logging;
using RequestBatcher.Diagnostics.Metrics;
using RequestBatcher.Diagnostics.Tracing;
using RequestBatcher.Handlers;
using RequestBatcher.PendingRequests;
using RequestBatcher.Scheduling;

namespace RequestBatcher;

/// <summary>
/// Transparently coalesces concurrent requests into batches and processes batches with bounded concurrency.
/// </summary>
/// <typeparam name="TRequest">The request type.</typeparam>
internal sealed class RequestBatchCoordinator<TRequest> : IRequestBatcher<TRequest>, IAsyncDisposable
{
    internal const string TopicName = "request-batcher";
    internal const string ConsumerGroupName = "request-batcher-consumers";
    private const int Running = 0;
    private const int Stopping = 1;
    private const int Stopped = 2;

    private readonly object _lifecycleLock = new();
    private readonly ILogger<RequestBatchCoordinator<TRequest>> _logger;
    private readonly RequestBatchOptions<TRequest> _options;
    private readonly RequestBatcherMetrics _metrics;
    private readonly RequestBatcherMetadata _metadata;
    private readonly ConcurrentDictionary<PendingBatchRequest<TRequest>, byte> _pendingRequests = new();
    private readonly Action<PendingBatchRequest<TRequest>> _onRequestFinished;
    private readonly PendingRequestProducer<TRequest> _producer;
    private readonly CancellationTokenSource _producerCancellation = new();
    private readonly CancellationTokenSource _consumerCancellation = new();
    private readonly BatchDispatchLoop<TRequest> _batchDispatchLoop;
    private readonly Task _dispatchLoopTask;
    private readonly Task _dispatchLoopMonitorTask;

    private Task? _disposeTask;
    private Exception? _consumerFailure;
    private TaskCompletionSource _drained = CreateCompletedSource();
    private int _pendingRequestCount;
    private int _state;

    public RequestBatchCoordinator(
        IBufferQueue bufferQueue,
        [FromKeyedServices] RequestBatchHandler<TRequest> handler,
        IOptions<RequestBatchOptions<TRequest>> options,
        ILogger<RequestBatchCoordinator<TRequest>> logger,
        RequestBatcherMetrics metrics,
        [FromKeyedServices] RequestBatcherConfiguration<TRequest> configuration)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        var topicName = configuration.TopicName;
        _metadata = configuration.Metadata;
        _logger = logger;
        _options = (configuration.Options ?? options.Value).ValidateAndClone();
        _metrics = metrics;
        _onRequestFinished = OnRequestFinished;

        _producer = new PendingRequestProducer<TRequest>(
            bufferQueue.GetProducer<PendingBatchRequest<TRequest>>(topicName),
            _logger,
            _metadata.RequestTypeName,
            _options.MaxPendingRequests,
            _producerCancellation.Token);

        _batchDispatchLoop = new BatchDispatchLoop<TRequest>(
            handler,
            _options.MaxConcurrency,
            _logger,
            _metadata,
            _metrics);
        var consumer = bufferQueue.CreatePullConsumers<PendingBatchRequest<TRequest>>(
            new BufferPullConsumerOptions
            {
                TopicName = topicName,
                GroupName = ConsumerGroupName,
                BatchSize = _options.BatchSize,
                AutoCommit = true,
            },
            1)
            .Single();

        _dispatchLoopTask = ExecutionContextIsolation.Run(
            () => _batchDispatchLoop.RunAsync(consumer, _consumerCancellation.Token));

        _logger.CoordinatorStarted(
            _metadata.RequestTypeName,
            _options.BatchSize,
            _options.MaxConcurrency,
            _options.MaxPendingRequests,
            _options.FullMode);

        // An already-completed loop can run failure handling before the monitor's first await yields.
        _dispatchLoopMonitorTask = ExecutionContextIsolation.Run(MonitorDispatchLoopAsync);
    }

    /// <inheritdoc />
    public Task ExecuteAsync(TRequest request, CancellationToken cancellationToken = default) =>
        ExecuteCall(
            RequestBatcherMetricTags.SingleSubmission,
            (Batcher: this, Request: request, Token: cancellationToken),
            static (state, context, observer) => state.Batcher.ExecuteCoreAsync(state.Request, null, state.Token, context, observer));

    internal Task ExecuteCoreAsync(
        TRequest request,
        IRequestCompletion? completion,
        CancellationToken cancellationToken,
        SubmissionTraceContext? traceContext = default,
        IRequestCompletionObserver? completionObserver = null)
    {
        if (Volatile.Read(ref _state) != Running)
        {
            return Task.FromException(CreateStoppedException());
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled(cancellationToken);
        }

        return AcceptRequest(request, completion, cancellationToken, traceContext, completionObserver);
    }

    /// <inheritdoc />
    public Task ExecuteAsync(
        IEnumerable<TRequest> requests,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requests);
        return ExecuteCall(
            RequestBatcherMetricTags.MultiSubmission,
            (Batcher: this, Requests: requests, Token: cancellationToken),
            static (state, context, observer) => state.Batcher.ExecuteCoreAsync(state.Requests, state.Token, context, observer));
    }

    internal Task ExecuteCoreAsync(
        IEnumerable<TRequest> requests,
        CancellationToken cancellationToken,
        SubmissionTraceContext? traceContext = default,
        IRequestCompletionObserver? completionObserver = null)
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
            _metrics.RecordSubmissionSize(_metadata.RequestTypeName, _metadata.BatcherName, RequestBatcherMetricTags.MultiSubmission, 0);
            return Task.CompletedTask;
        }

        return AcceptBatch(requestArray, cancellationToken, traceContext, completionObserver);
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
        CancellationToken cancellationToken,
        SubmissionTraceContext? traceContext,
        IRequestCompletionObserver? completionObserver)
    {
        PendingBatchRequest<TRequest> pendingRequest;
        lock (_lifecycleLock)
        {
            if (_state != Running)
            {
                return Task.FromException(CreateStoppedException());
            }

            TrackRequestsLocked(1);
            if (completion is null && completionObserver is not null)
            {
                completion = new TracedRequestCompletion(completionObserver);
            }

            pendingRequest = CreatePendingRequest(request, cancellationToken, completion, traceContext);
            _pendingRequests.TryAdd(pendingRequest, 0);
        }

        _metrics.RecordSubmissionSize(_metadata.RequestTypeName, _metadata.BatcherName, RequestBatcherMetricTags.SingleSubmission, 1);
        return _producer.ProduceAsync(pendingRequest, cancellationToken);
    }

    private Task AcceptBatch(
        TRequest[] requests,
        CancellationToken cancellationToken,
        SubmissionTraceContext? traceContext,
        IRequestCompletionObserver? completionObserver)
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
            batchCompletion = completionObserver is null
                ? new BatchSubmissionCompletion(requests.Length, cancellationToken)
                : new TracedBatchSubmissionCompletion(requests.Length, cancellationToken, completionObserver);
            pendingRequests = new PendingBatchRequest<TRequest>[requests.Length];
            for (var i = 0; i < requests.Length; i++)
            {
                pendingRequests[i] = CreatePendingRequest(requests[i], cancellationToken, batchCompletion, traceContext);
                _pendingRequests.TryAdd(pendingRequests[i], 0);
            }
        }

        _metrics.RecordSubmissionSize(_metadata.RequestTypeName, _metadata.BatcherName, RequestBatcherMetricTags.MultiSubmission, requests.Length);
        return _producer.ProduceAsync(pendingRequests, cancellationToken);
    }

    private PendingBatchRequest<TRequest> CreatePendingRequest(
        TRequest request,
        CancellationToken cancellationToken,
        IRequestCompletion? completion,
        SubmissionTraceContext? traceContext) =>
        traceContext is not null
            ? new TracedPendingBatchRequest<TRequest>(request, cancellationToken, _onRequestFinished, completion, traceContext)
            : new PendingBatchRequest<TRequest>(request, cancellationToken, _onRequestFinished, completion);

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
            _metadata.RequestTypeName,
            Volatile.Read(ref _pendingRequestCount));
        try
        {
            await pendingRequestsDrained.ConfigureAwait(false);
            await _batchDispatchLoop.DrainAsync().ConfigureAwait(false);
            await _consumerCancellation.CancelAsync().ConfigureAwait(false);
            await _dispatchLoopTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            await _dispatchLoopMonitorTask.ConfigureAwait(false);

            if (_consumerFailure is not null)
            {
                ExceptionDispatchInfo.Capture(_consumerFailure).Throw();
            }

            await _dispatchLoopTask.ConfigureAwait(false);
        }
        finally
        {
            try
            {
                await _dispatchLoopMonitorTask.ConfigureAwait(false);
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

                _logger.CoordinatorStopped(_metadata.RequestTypeName);
            }
        }
    }

    private async Task MonitorDispatchLoopAsync()
    {
        await _dispatchLoopTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        if (!_consumerCancellation.IsCancellationRequested)
        {
            HandleConsumerFailure(_dispatchLoopTask.Exception?.GetBaseException() ??
                new InvalidOperationException(
                    $"A request batch consumer for '{_metadata.RequestTypeName}' stopped unexpectedly."));
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

    private Task ExecuteCall<TState>(
        string kind,
        TState state,
        Func<TState, SubmissionTraceContext?, IRequestCompletionObserver?, Task> submit)
    {
        return _metrics.ExecuteEnabled
            ? _metrics.MeasureExecution(
                _metadata.RequestTypeName,
                _metadata.BatcherName,
                kind,
                (Coordinator: this, State: state, Submit: submit),
                static args => args.Coordinator.ExecuteWithTracing(args.State, args.Submit))
            : ExecuteWithTracing(state, submit);
    }

    private Task ExecuteWithTracing<TState>(
        TState state,
        Func<TState, SubmissionTraceContext?, IRequestCompletionObserver?, Task> submit) =>
        RequestBatcherTracing.Source.HasListeners()
            ? RequestBatcherTracing.TraceCall(_metadata, state, submit)
            : submit(state, null, null);

    private static TaskCompletionSource CreateCompletedSource()
    {
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.SetResult();
        return source;
    }
}
