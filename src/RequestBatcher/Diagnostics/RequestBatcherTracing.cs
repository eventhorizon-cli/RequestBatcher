using System.Diagnostics;
using RequestBatcher.PendingRequests;

namespace RequestBatcher.Diagnostics;

internal static class RequestBatcherTracing
{
    internal const string SourceName = "RequestBatcher";
    internal const string CallName = "RequestBatcher.Execute";
    internal const string BatchName = "RequestBatcher.ProcessBatch";
    internal static readonly ActivitySource Source = new(SourceName);

    public static Task TraceCall<TState>(
        string requestTypeName,
        TState state,
        Func<TState, ActivityContext, Task> submit,
        string? batcherName = null)
    {
        var parent = Activity.Current;
        var activity = StartCall(requestTypeName, batcherName);
        try
        {
            var task = submit(state, activity?.Context ?? parent?.Context ?? default);
            if (activity is null)
            {
                return task;
            }

            if (task.IsCompleted)
            {
                EndCall(activity, task);
                return task;
            }

            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = CompleteCallAsync(task, activity, completion);
            return completion.Task;
        }
        catch (Exception exception)
        {
            RecordFailure(activity, exception);
            activity?.Dispose();
            throw;
        }
        finally
        {
            // The Activity remains alive until completion, but must not become the caller's ambient span.
            Activity.Current = parent;
        }
    }

    public static Task<TResult> TraceCall<TState, TResult>(
        string requestTypeName,
        TState state,
        Func<TState, ActivityContext, Task<TResult>> submit,
        string? batcherName = null)
    {
        var parent = Activity.Current;
        var activity = StartCall(requestTypeName, batcherName);
        try
        {
            var task = submit(state, activity?.Context ?? parent?.Context ?? default);
            if (activity is null)
            {
                return task;
            }

            if (task.IsCompleted)
            {
                EndCall(activity, task);
                return task;
            }

            var completion = new TaskCompletionSource<TResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = CompleteCallAsync(task, activity, completion);
            return completion.Task;
        }
        catch (Exception exception)
        {
            RecordFailure(activity, exception);
            activity?.Dispose();
            throw;
        }
        finally
        {
            Activity.Current = parent;
        }
    }

    public static Activity? StartBatch<TRequest>(
        string requestTypeName,
        IReadOnlyList<PendingBatchRequest<TRequest>> requests,
        string? batcherName = null)
    {
        if (!Source.HasListeners())
        {
            return null;
        }

        List<ActivityLink>? links = null;
        HashSet<(ActivityTraceId, ActivitySpanId)>? contexts = null;
        foreach (var request in requests)
        {
            if (request is not TracedPendingBatchRequest<TRequest> traced)
            {
                continue;
            }

            var context = traced.Context;
            contexts ??= [];
            if (contexts.Add((context.TraceId, context.SpanId)))
            {
                (links ??= []).Add(new ActivityLink(context));
            }
        }

        var tags = CreateTags(requestTypeName, batcherName);
        tags.Add("requestbatcher.batch.size", requests.Count);

        // Dispatch runs in an isolated context. Links describe the many-to-many relationship with calls.
        return Source.StartActivity(
            GetBatchName(batcherName),
            ActivityKind.Consumer,
            parentContext: default,
            tags: tags,
            links: links);
    }

    public static void RecordFailure(Activity? activity, Exception exception)
    {
        activity?.SetStatus(ActivityStatusCode.Error);
        activity?.SetTag("error.type", exception.GetType().FullName);
    }

    private static Activity? StartCall(string requestTypeName, string? batcherName) => Source.StartActivity(
        GetCallName(batcherName),
        ActivityKind.Internal,
        parentContext: Activity.Current?.Context ?? default,
        tags: CreateTags(requestTypeName, batcherName));

    internal static string GetCallName(string? batcherName) => GetSpanName(CallName, batcherName);

    internal static string GetBatchName(string? batcherName) => GetSpanName(BatchName, batcherName);

    private static string GetSpanName(string baseName, string? batcherName) =>
        batcherName is null ? baseName : $"{baseName} {batcherName}";

    private static ActivityTagsCollection CreateTags(string requestTypeName, string? batcherName)
    {
        var tags = new ActivityTagsCollection { { "requestbatcher.request.type", requestTypeName } };
        if (batcherName is not null)
        {
            tags.Add("requestbatcher.name", batcherName);
        }

        return tags;
    }

    private static void EndCall(Activity activity, Task task)
    {
        if (task.IsFaulted)
        {
            RecordFailure(activity, task.Exception!.InnerExceptions[0]);
        }
        else if (task.IsCanceled)
        {
            activity.SetTag("requestbatcher.canceled", true);
        }

        activity.Dispose();
    }

    private static async Task CompleteCallAsync(Task task, Activity activity, TaskCompletionSource completion)
    {
        await task.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        EndCall(activity, task);
        if (task.IsFaulted)
        {
            // Preserve all original faults, including faulted OperationCanceledException instances.
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

    private static async Task CompleteCallAsync<TResult>(
        Task<TResult> task,
        Activity activity,
        TaskCompletionSource<TResult> completion)
    {
        await ((Task)task).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        EndCall(activity, task);
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
}
