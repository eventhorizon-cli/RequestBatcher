using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace RequestBatcher.Diagnostics;

/// <summary>
/// Records low-cardinality RequestBatcher metrics when a meter listener is present.
/// </summary>
internal static class RequestBatcherMetrics
{
    internal const string MeterName = "RequestBatcher";

    private static readonly Meter _meter = new(MeterName);
    private static readonly Histogram<long> _batchSize = _meter.CreateHistogram<long>(
        RequestBatcherMetricNames.BatchSize,
        unit: "{request}",
        description: "Request items per completed Handler batch, including failed attempts. Sum counts items; count counts batches.");
    private static readonly Histogram<long> _submissionSize = _meter.CreateHistogram<long>(
        RequestBatcherMetricNames.SubmissionSize,
        unit: "{request}",
        description: "Request items per submission reaching coordinator admission, including empty collections and later failures. Sum counts items; count counts submissions. Submission kind is single for one-item overloads and multi for collection overloads.");
    private static readonly Histogram<double> _handleDuration = _meter.CreateHistogram<double>(
        RequestBatcherMetricNames.HandleDuration,
        unit: "ms",
        description: "Handler execution and scope disposal duration per batch, including failed attempts. Excludes queueing and publishing results.");
    private static readonly Histogram<double> _executeDuration = _meter.CreateHistogram<double>(
        RequestBatcherMetricNames.ExecuteDuration,
        unit: "ms",
        description: "End-to-end duration per public ExecuteAsync call, including capacity waiting, queueing, all Handler batches, and response assembly. Submission kind is single for one-item overloads and multi for collection overloads. Outcome is success, error, or canceled.");

    internal static bool ExecuteEnabled => _executeDuration.Enabled;

    internal static Task MeasureExecution<TState>(
        string requestTypeName, string? batcherName, string kind, TState state, Func<TState, Task> submit)
    {
        var scope = new ExecutionScope(requestTypeName, batcherName, kind);
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

    internal static Task<TResult> MeasureExecution<TState, TResult>(
        string requestTypeName, string? batcherName, string kind, TState state, Func<TState, Task<TResult>> submit)
    {
        var scope = new ExecutionScope(requestTypeName, batcherName, kind);
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

    private readonly struct ExecutionScope(string requestTypeName, string? batcherName, string kind)
    {
        private readonly long _startedAt = Stopwatch.GetTimestamp();

        internal void Record(string outcome)
        {
            if (!_executeDuration.Enabled)
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

            _executeDuration.Record(elapsed, tags);
        }
    }

    internal static void RecordSubmissionSize(string requestTypeName, string? batcherName, string kind, int count)
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

    internal static BatchScope StartBatch(string requestTypeName, string? batcherName, int count)
    {
        // Skip tag preparation and timing state when none of the batch instruments has subscribers.
        if (!_batchSize.Enabled && !_handleDuration.Enabled)
        {
            return default;
        }

        return new BatchScope(requestTypeName, batcherName, count, Stopwatch.GetTimestamp());
    }

    internal readonly struct BatchScope : IDisposable
    {
        private readonly string? _requestTypeName;
        private readonly string? _batcherName;
        private readonly int _count;
        private readonly long _startedAt;
        private readonly bool _enabled;

        internal BatchScope(string requestTypeName, string? batcherName, int count, long startedAt)
        {
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
            if (_batchSize.Enabled)
            {
                _batchSize.Record(_count, tags);
            }

            // Keep elapsed-time calculation inside the guard so it runs only when duration is collected.
            if (_handleDuration.Enabled)
            {
                _handleDuration.Record(Stopwatch.GetElapsedTime(_startedAt).TotalMilliseconds, tags);
            }
        }
    }
}
