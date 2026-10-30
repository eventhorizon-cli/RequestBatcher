using System.Diagnostics;
using RequestBatcher.PendingRequests;

namespace RequestBatcher.Diagnostics.Tracing;

internal static class RequestBatcherTracing
{
    internal const string SourceName = "RequestBatcher";
    internal const string CallName = "RequestBatcher.Execute";
    internal const string BatchName = "RequestBatcher.Handle";
    internal static readonly ActivitySource Source = new(SourceName);

    public static Task TraceCall<TState>(
        RequestBatcherMetadata metadata,
        TState state,
        Func<TState, SubmissionTraceContext?, Task> submit) =>
        TraceCall(metadata, (state, submit), static (args, context, _) => args.submit(args.state, context), observeCompletion: false);

    public static Task<TResult> TraceCall<TState, TResult>(
        RequestBatcherMetadata metadata,
        TState state,
        Func<TState, SubmissionTraceContext?, Task<TResult>> submit) =>
        TraceCall(metadata, (state, submit), static (args, context, _) => args.submit(args.state, context), observeCompletion: false);

    public static Task TraceCall<TState>(
        RequestBatcherMetadata metadata,
        TState state,
        Func<TState, SubmissionTraceContext?, IRequestCompletionObserver?, Task> submit,
        bool observeCompletion = true)
    {
        var parent = Activity.Current;
        var activity = StartCall(metadata);
        var context = CreateContext(activity, parent);
        var observer = activity is null || !observeCompletion ? null : new CallCompletionObserver(activity, context);
        try
        {
            var task = submit(state, context, observer);
            if (observer?.TryAttach(task) == true)
            {
                return task;
            }
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
            observer?.TryAttach(null);
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
        RequestBatcherMetadata metadata,
        TState state,
        Func<TState, SubmissionTraceContext?, IRequestCompletionObserver?, Task<TResult>> submit,
        bool observeCompletion = true)
    {
        var parent = Activity.Current;
        var activity = StartCall(metadata);
        var context = CreateContext(activity, parent);
        var observer = activity is null || !observeCompletion ? null : new CallCompletionObserver(activity, context);
        try
        {
            var task = submit(state, context, observer);
            if (observer?.TryAttach(task) == true)
            {
                return task;
            }
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
            observer?.TryAttach(null);
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

        BatchTraceLinks? links = null;
        // A sampled Execute context enables links to every participating context, including unsampled ones.
        // With no sampled context, omit the collector; the optional linked-batch sampler treats no links as Drop.
        if (HasSampledSubmission(requests))
        {
            foreach (var request in requests)
            {
                if (request is not TracedPendingBatchRequest<TRequest> traced)
                {
                    continue;
                }

                if (links is null)
                {
                    links = new BatchTraceLinks(traced.TraceContext);
                }
                else
                {
                    links.Add(traced.TraceContext);
                }
            }
        }

        var tags = CreateTags(requestTypeName, batcherName);
        tags.Add(RequestBatcherTraceTags.BatchSize, requests.Count);

        // Dispatch runs in an isolated context. Links describe the many-to-many relationship with calls.
        // Listeners sample these creation-time inputs before allocating the Activity object. Still call StartActivity
        // when no Execute is sampled: the SDK may retain an unsampled root Activity for propagation to Handler children.
        // A non-null Activity therefore does not imply recording; collect additional data only when requested below.
        var activity = Source.StartActivity(
            GetBatchName(batcherName),
            ActivityKind.Consumer,
            parentContext: default,
            tags: tags,
            links: links);
        if (activity is not null)
        {
            links?.AddBatch(activity.Context);
            if (activity.IsAllDataRequested)
            {
                activity.SetTag(RequestBatcherTraceTags.SubmissionCount, CountSubmissions(requests, links));
            }
        }

        return activity;
    }

    private static bool HasSampledSubmission<TRequest>(IReadOnlyList<PendingBatchRequest<TRequest>> requests)
    {
        foreach (var request in requests)
        {
            if (request is TracedPendingBatchRequest<TRequest> traced &&
                (traced.TraceContext.Context.TraceFlags & ActivityTraceFlags.Recorded) != 0)
            {
                return true;
            }
        }

        return false;
    }

    private static int CountSubmissions<TRequest>(
        IReadOnlyList<PendingBatchRequest<TRequest>> requests,
        BatchTraceLinks? links)
    {
        // A unique context for every item already proves that every submission is distinct.
        if (links?.Count == requests.Count)
        {
            return requests.Count;
        }

        if (requests.Count == 0)
        {
            return 0;
        }

        var first = requests[0].Completion;
        HashSet<Task>? additional = null;
        for (var index = 1; index < requests.Count; index++)
        {
            var submission = requests[index].Completion;
            if (!ReferenceEquals(submission, first))
            {
                (additional ??= []).Add(submission);
            }
        }

        return 1 + (additional?.Count ?? 0);
    }

    public static void RecordFailure(Activity? activity, Exception exception)
    {
        activity?.SetStatus(ActivityStatusCode.Error);
        activity?.SetTag(RequestBatcherTraceTags.ErrorType, exception.GetType().Name);
    }

    private static Activity? StartCall(RequestBatcherMetadata metadata) => Source.StartActivity(
        metadata.TraceCallName,
        ActivityKind.Internal,
        parentContext: Activity.Current?.Context ?? default,
        tags: metadata.TraceTags);

    internal static string GetCallName(string? batcherName) => GetSpanName(CallName, batcherName);

    internal static string GetBatchName(string? batcherName) => GetSpanName(BatchName, batcherName);

    private static string GetSpanName(string baseName, string? batcherName) =>
        batcherName is null ? baseName : $"{baseName} {batcherName}";

    private static ActivityTagsCollection CreateTags(string requestTypeName, string? batcherName)
    {
        var tags = new ActivityTagsCollection { { RequestBatcherTraceTags.RequestType, requestTypeName } };
        if (batcherName is not null)
        {
            tags.Add(RequestBatcherTraceTags.BatcherName, batcherName);
        }

        return tags;
    }

    private static SubmissionTraceContext? CreateContext(Activity? activity, Activity? parent)
    {
        var context = activity?.Context ?? parent?.Context ?? default;
        return context.TraceId != default && context.SpanId != default
            ? SubmissionTraceContext.Create(context, activity is { Recorded: true, IsAllDataRequested: true })
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
            activity.SetTag(RequestBatcherTraceTags.Canceled, true);
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
