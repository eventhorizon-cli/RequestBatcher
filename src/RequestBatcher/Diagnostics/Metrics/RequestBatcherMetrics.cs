using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using BufferQueue;

namespace RequestBatcher.Diagnostics.Metrics;

/// <summary>
/// Records low-cardinality RequestBatcher metrics when a meter listener is present.
/// </summary>
internal sealed class RequestBatcherMetrics : IAsyncDisposable
{
    internal const string MeterName = "RequestBatcher";

    private const int MaximumBucketCount = 25;
    private const int SubmissionDoublings = 5;

    private readonly ConcurrentDictionary<BacklogRegistration, byte> _backlogSources = new();
    private readonly Meter _meter;
    private readonly Histogram<long> _batchSize;
    private readonly Histogram<long> _submissionSize;
    private readonly Histogram<double> _handleDuration;
    private readonly Histogram<double> _executeDuration;
    private readonly object _lifecycleLock = new();
    private TaskCompletionSource? _disposeCompletion;
    private TaskCompletionSource? _executionsDrained;
    private int _activeExecutions;
    private int _disposed;

    public RequestBatcherMetrics(IEnumerable<RequestBatcherMetricsConfiguration> configurations)
    {
        var maximumBatchSize = configurations.Max(configuration => configuration.BatchSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBatchSize);

        _meter = new Meter(MeterName);
        _batchSize = _meter.CreateHistogram<long>(
            RequestBatcherMetricNames.BatchSize,
            unit: "{request}",
            description: "Request items per completed Handler batch, including failed attempts. Sum counts items; count counts batches.",
            advice: new InstrumentAdvice<long> { HistogramBucketBoundaries = CreateBatchSizeBounds(maximumBatchSize) });
        _submissionSize = _meter.CreateHistogram<long>(
            RequestBatcherMetricNames.SubmissionSize,
            unit: "{request}",
            description: "Request items per submission reaching coordinator admission, including empty collections and later failures. Sum counts items; count counts submissions. Submission kind is single for one-item overloads and multi for collection overloads.",
            advice: new InstrumentAdvice<long> { HistogramBucketBoundaries = CreateSubmissionSizeBounds(maximumBatchSize) });
        _handleDuration = _meter.CreateHistogram<double>(
            RequestBatcherMetricNames.HandleDuration,
            unit: "ms",
            description: "Handler execution and scope disposal duration per batch, including failed attempts. Excludes queueing and publishing results.");
        _executeDuration = _meter.CreateHistogram<double>(
            RequestBatcherMetricNames.ExecuteDuration,
            unit: "ms",
            description: "End-to-end duration per public ExecuteAsync call, including capacity waiting, queueing, all Handler batches, and response assembly. Submission kind is single for one-item overloads and multi for collection overloads. Outcome is success, error, or canceled.");
        _meter.CreateObservableGauge(
            RequestBatcherMetricNames.Backlog,
            ObserveBacklog,
            unit: "{request}",
            description: "Approximate request items already enqueued and not yet pulled, sampled at collection time. Excludes producer capacity waits and active Handler work; includes canceled records until pulled. Unavailable snapshots are omitted.");
    }

    private static long[] CreateBatchSizeBounds(int maximumBatchSize)
    {
        var bounds = new SortedSet<long> { 0, 1 };
        for (var step = 1; step <= MaximumBucketCount - 3; step++)
        {
            bounds.Add(DivideCeiling((long)maximumBatchSize * step, MaximumBucketCount - 3));
        }

        return [.. bounds];
    }

    private static long[] CreateSubmissionSizeBounds(int maximumBatchSize)
    {
        var steps = MaximumBucketCount - 3 - SubmissionDoublings;
        var bounds = new SortedSet<long> { 0, 1 };
        for (var step = 1; step <= steps; step++)
        {
            bounds.Add(DivideCeiling((long)maximumBatchSize * step, steps));
        }

        for (var doubling = 1; doubling <= SubmissionDoublings; doubling++)
        {
            bounds.Add((long)maximumBatchSize * (1L << doubling));
        }

        return [.. bounds];
    }

    private static long DivideCeiling(long numerator, int denominator) =>
        (numerator + denominator - 1) / denominator;

    internal IDisposable RegisterBacklog<TRequest>(
        IBufferPullConsumer<TRequest> consumer, string requestTypeName, string? batcherName)
    {
        // One AutoCommit consumer already aggregates all partitions for this batcher. Read only during collection.
        var registration = new BacklogRegistration(this,
            () => consumer.TryGetLagSnapshot(out var snapshot) && snapshot.Unit == BufferOffsetUnit.Items
                ? snapshot.OffsetLag : null,
            requestTypeName, batcherName);
        _backlogSources.TryAdd(registration, 0);
        if (Volatile.Read(ref _disposed) != 0)
        {
            registration.Dispose();
            throw new ObjectDisposedException(nameof(RequestBatcherMetrics));
        }

        return registration;
    }

    private IEnumerable<Measurement<long>> ObserveBacklog()
    {
        foreach (var source in _backlogSources)
        {
            if (source.Key.TryObserve(out var measurement))
            {
                yield return measurement;
            }
        }
    }

    private sealed class BacklogRegistration : IDisposable
    {
        private readonly KeyValuePair<string, object?>[] _tags;
        private readonly RequestBatcherMetrics _owner;
        private Func<ulong?>? _read;

        internal BacklogRegistration(RequestBatcherMetrics owner, Func<ulong?> read, string requestTypeName, string? batcherName)
        {
            _owner = owner;
            _read = read;
            _tags = batcherName is null
                ? [new(RequestBatcherMetricTags.RequestType, requestTypeName)]
                : [new(RequestBatcherMetricTags.RequestType, requestTypeName), new(RequestBatcherMetricTags.BatcherName, batcherName)];
        }

        internal bool TryObserve(out Measurement<long> measurement)
        {
            var read = Volatile.Read(ref _read);
            if (read?.Invoke() is { } value)
            {
                // Preserve a nonnegative value even for an upstream saturated ulong snapshot.
                measurement = new Measurement<long>((long)Math.Min(value, (ulong)long.MaxValue), _tags);
                return true;
            }

            measurement = default;
            return false;
        }

        public void Dispose()
        {
            // A collection already in progress may finish; later collections must not retain or query this consumer.
            Interlocked.Exchange(ref _read, null);
            _owner._backlogSources.TryRemove(this, out _);
        }
    }

    public ValueTask DisposeAsync()
    {
        Task drained;
        TaskCompletionSource completion;
        lock (_lifecycleLock)
        {
            if (_disposeCompletion is not null)
            {
                return new ValueTask(_disposeCompletion.Task);
            }

            Volatile.Write(ref _disposed, 1);
            completion = _disposeCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            drained = _activeExecutions == 0
                ? Task.CompletedTask
                : (_executionsDrained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        }

        // Meter callbacks and completion continuations must run outside the lifecycle lock.
        _ = DisposeCoreAsync(drained, completion);
        return new ValueTask(completion.Task);
    }

    private async Task DisposeCoreAsync(Task drained, TaskCompletionSource completion)
    {
        try
        {
            await drained.ConfigureAwait(false);
            foreach (var source in _backlogSources.Keys)
            {
                source.Dispose();
            }

            _meter.Dispose();
            completion.TrySetResult();
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
    }

    private bool TryStartExecution()
    {
        lock (_lifecycleLock)
        {
            if (_disposed != 0)
            {
                return false;
            }

            _activeExecutions++;
            return true;
        }
    }

    private void CompleteExecution()
    {
        TaskCompletionSource? drained = null;
        lock (_lifecycleLock)
        {
            if (--_activeExecutions == 0)
            {
                drained = _executionsDrained;
            }
        }

        drained?.TrySetResult();
    }

    internal bool ExecuteEnabled => _executeDuration.Enabled;

    internal Task MeasureExecution<TState>(
        string requestTypeName, string? batcherName, string kind, TState state, Func<TState, Task> submit)
    {
        if (!TryStartExecution())
        {
            return submit(state);
        }

        var scope = new ExecutionScope(this, requestTypeName, batcherName, kind);
        Task task;
        try
        {
            task = submit(state);
        }
        catch
        {
            scope.Record(RequestBatcherMetricTags.Error);
            throw;
        }

        if (task.IsCompleted)
        {
            scope.Record(GetOutcome(task));
            return task;
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = CompleteExecutionAsync(task, scope, completion);
        return completion.Task;
    }

    internal Task<TResult> MeasureExecution<TState, TResult>(
        string requestTypeName, string? batcherName, string kind, TState state, Func<TState, Task<TResult>> submit)
    {
        if (!TryStartExecution())
        {
            return submit(state);
        }

        var scope = new ExecutionScope(this, requestTypeName, batcherName, kind);
        Task<TResult> task;
        try
        {
            task = submit(state);
        }
        catch
        {
            scope.Record(RequestBatcherMetricTags.Error);
            throw;
        }

        if (task.IsCompleted)
        {
            scope.Record(GetOutcome(task));
            return task;
        }

        var completion = new TaskCompletionSource<TResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = CompleteExecutionAsync(task, scope, completion);
        return completion.Task;
    }

    private static string GetOutcome(Task task) => task.IsFaulted
        ? RequestBatcherMetricTags.Error
        : task.IsCanceled ? RequestBatcherMetricTags.Canceled : RequestBatcherMetricTags.Success;

    private static async Task CompleteExecutionAsync(Task task, ExecutionScope scope, TaskCompletionSource completion)
    {
        await task.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        try
        {
            scope.Record(GetOutcome(task));
        }
        finally
        {
            // Preserve all faults and distinguish faulted cancellation exceptions from canceled Tasks.
            if (task.IsFaulted)
            {
                completion.TrySetException(task.Exception!.InnerExceptions);
            }
            else if (task.IsCanceled)
            {
                completion.TrySetCanceled(GetCancellationToken(task));
            }
            else
            {
                completion.TrySetResult();
            }
        }
    }

    private static async Task CompleteExecutionAsync<TResult>(
        Task<TResult> task, ExecutionScope scope, TaskCompletionSource<TResult> completion)
    {
        await ((Task)task).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        try
        {
            scope.Record(GetOutcome(task));
        }
        finally
        {
            if (task.IsFaulted)
            {
                completion.TrySetException(task.Exception!.InnerExceptions);
            }
            else if (task.IsCanceled)
            {
                completion.TrySetCanceled(GetCancellationToken(task));
            }
            else
            {
                completion.TrySetResult(task.Result);
            }
        }
    }

    private static CancellationToken GetCancellationToken(Task task)
    {
        try
        {
            task.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException exception)
        {
            return exception.CancellationToken;
        }

        return default;
    }

    private readonly struct ExecutionScope(RequestBatcherMetrics metrics, string requestTypeName, string? batcherName, string kind)
    {
        private readonly long _startedAt = Stopwatch.GetTimestamp();

        internal void Record(string outcome)
        {
            try
            {
                if (!metrics._executeDuration.Enabled)
                {
                    return;
                }

                var elapsed = Stopwatch.GetElapsedTime(_startedAt).TotalMilliseconds;
                var tags = new TagList
                {
                    { RequestBatcherMetricTags.RequestType, requestTypeName },
                    { RequestBatcherMetricTags.SubmissionKind, kind },
                    { RequestBatcherMetricTags.Outcome, outcome },
                };
                if (batcherName is not null)
                {
                    tags.Add(RequestBatcherMetricTags.BatcherName, batcherName);
                }

                metrics._executeDuration.Record(elapsed, tags);
            }
            finally
            {
                metrics.CompleteExecution();
            }
        }
    }

    internal void RecordSubmissionSize(string requestTypeName, string? batcherName, string kind, int count)
    {
        if (!_submissionSize.Enabled)
        {
            return;
        }

        var tags = new TagList
        {
            { RequestBatcherMetricTags.RequestType, requestTypeName },
            { RequestBatcherMetricTags.SubmissionKind, kind },
        };
        if (batcherName is not null)
        {
            tags.Add(RequestBatcherMetricTags.BatcherName, batcherName);
        }

        _submissionSize.Record(count, tags);
    }

    internal BatchScope StartBatch(string requestTypeName, string? batcherName, int count)
    {
        // Skip tag preparation and timing state when none of the batch instruments has subscribers.
        if (!_batchSize.Enabled && !_handleDuration.Enabled)
        {
            return default;
        }

        return new BatchScope(this, requestTypeName, batcherName, count, Stopwatch.GetTimestamp());
    }

    internal readonly struct BatchScope : IDisposable
    {
        private readonly RequestBatcherMetrics? _metrics;
        private readonly string? _requestTypeName;
        private readonly string? _batcherName;
        private readonly int _count;
        private readonly long _startedAt;
        private readonly bool _enabled;

        internal BatchScope(RequestBatcherMetrics metrics, string requestTypeName, string? batcherName, int count, long startedAt)
        {
            _metrics = metrics;
            _requestTypeName = requestTypeName;
            _batcherName = batcherName;
            _count = count;
            _startedAt = startedAt;
            _enabled = true;
        }

        public void Dispose()
        {
            if (!_enabled)
            {
                return;
            }

            var tags = new TagList { { RequestBatcherMetricTags.RequestType, _requestTypeName! } };
            if (_batcherName is not null)
            {
                tags.Add(RequestBatcherMetricTags.BatcherName, _batcherName);
            }

            // Enabled reflects each instrument's listener subscriptions, which can differ within the same Meter.
            // These guards skip unsubscribed recording calls; the instruments also handle no subscribers internally.
            if (_metrics!._batchSize.Enabled)
            {
                _metrics._batchSize.Record(_count, tags);
            }

            // Keep elapsed-time calculation inside the guard so it runs only when duration is collected.
            if (_metrics._handleDuration.Enabled)
            {
                _metrics._handleDuration.Record(Stopwatch.GetElapsedTime(_startedAt).TotalMilliseconds, tags);
            }
        }
    }
}
