using System.Diagnostics;
using RequestBatcher.PendingRequests;

namespace RequestBatcher.Diagnostics;

internal static class RequestBatcherTracing
{
    internal const string SourceName = "RequestBatcher";
    internal const string CallName = "RequestBatcher.Execute";
    internal const string BatchName = "RequestBatcher.HandleBatch";
    internal static readonly ActivitySource Source = new(SourceName);

    public static Task TraceCall<TState>(
        string requestTypeName,
        TState state,
        Func<TState, SubmissionTraceContext?, Task> submit,
        string? batcherName = null)
    {
        var parent = Activity.Current;
        var activity = StartCall(requestTypeName, batcherName);
        var context = CreateContext(activity, parent);
        try
        {
            var task = submit(state, context);
            if (activity is null)
            {
                return task;
            }

            if (task.IsCompleted)
            {
                EndCall(activity, task, context);
                return task;
            }

            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = CompleteCallAsync(task, activity, completion, context);
            return completion.Task;
        }
        catch (Exception exception)
        {
            RecordFailure(activity, exception);
            if (activity is not null)
            {
                context?.Complete(activity);
            }
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
        Func<TState, SubmissionTraceContext?, Task<TResult>> submit,
        string? batcherName = null)
    {
        var parent = Activity.Current;
        var activity = StartCall(requestTypeName, batcherName);
        var context = CreateContext(activity, parent);
        try
        {
            var task = submit(state, context);
            if (activity is null)
            {
                return task;
            }

            if (task.IsCompleted)
            {
                EndCall(activity, task, context);
                return task;
            }

            var completion = new TaskCompletionSource<TResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = CompleteCallAsync(task, activity, completion, context);
            return completion.Task;
        }
        catch (Exception exception)
        {
            RecordFailure(activity, exception);
            if (activity is not null)
            {
                context?.Complete(activity);
            }
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
        List<SubmissionTraceContext>? submissions = null;
        HashSet<(ActivityTraceId, ActivitySpanId)>? contexts = null;
        foreach (var request in requests)
        {
            if (request is not TracedPendingBatchRequest<TRequest> traced)
            {
                continue;
            }

            var context = traced.TraceContext.Context;
            contexts ??= [];
            if (contexts.Add((context.TraceId, context.SpanId)))
            {
                (links ??= []).Add(new ActivityLink(context));
                (submissions ??= []).Add(traced.TraceContext);
            }
        }

        var tags = CreateTags(requestTypeName, batcherName);
        tags.Add("requestbatcher.batch.size", requests.Count);

        // Dispatch runs in an isolated context. Links describe the many-to-many relationship with calls.
        var activity = Source.StartActivity(
            GetBatchName(batcherName),
            ActivityKind.Consumer,
            parentContext: default,
            tags: tags,
            links: links);
        if (activity is not null && submissions is not null)
        {
            foreach (var submission in submissions)
            {
                submission.AddBatch(activity.Context);
            }
        }

        return activity;
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

    private static SubmissionTraceContext? CreateContext(Activity? activity, Activity? parent)
    {
        var context = activity?.Context ?? parent?.Context ?? default;
        return context.TraceId != default && context.SpanId != default
            ? new SubmissionTraceContext(context, activity?.IsAllDataRequested == true)
            : null;
    }

    private static void EndCall(Activity activity, Task task, SubmissionTraceContext? context)
    {
        if (task.IsFaulted)
        {
            RecordFailure(activity, task.Exception!.InnerExceptions[0]);
        }
        else if (task.IsCanceled)
        {
            activity.SetTag("requestbatcher.canceled", true);
        }

        context?.Complete(activity);
        activity.Dispose();
    }

    private static async Task CompleteCallAsync(
        Task task, Activity activity, TaskCompletionSource completion, SubmissionTraceContext? context)
    {
        await task.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        EndCall(activity, task, context);
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
        TaskCompletionSource<TResult> completion,
        SubmissionTraceContext? context)
    {
        await ((Task)task).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        EndCall(activity, task, context);
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
