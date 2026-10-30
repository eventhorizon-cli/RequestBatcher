using RequestBatcher.Diagnostics;
using RequestBatcher.PendingRequests;

namespace RequestBatcher;

internal sealed class ResponseRequestBatcher<TRequest, TResponse>(
    RequestBatchCoordinator<RequestBatchEntry<TRequest, TResponse>> batcher)
    : IRequestBatcher<TRequest, TResponse>
{
    private static readonly string _telemetryRequestTypeName = RequestBatcherRequestType.GetName(typeof(TRequest));
    private readonly RequestBatcherCallDiagnostics _callDiagnostics = new(
        _telemetryRequestTypeName,
        batcher.BatcherName,
        batcher.Metrics);

    internal string? BatcherName => batcher.BatcherName;

    internal RequestBatcherMetrics Metrics => batcher.Metrics;

    public Task<TResponse> ExecuteAsync(TRequest request, CancellationToken cancellationToken = default) =>
        _callDiagnostics.Execute(
            RequestBatcherMetricTags.SingleSubmission,
            (Batcher: this, Request: request, Token: cancellationToken),
            static (state, context, observer) => state.Batcher.ExecuteCoreAsync(state.Request, state.Token, context, observer));

    internal Task<TResponse> ExecuteCoreAsync(
        TRequest request,
        CancellationToken cancellationToken,
        SubmissionTraceContext? traceContext = default,
        IRequestCompletionObserver? completionObserver = null)
    {
        var entry = new RequestBatchEntry<TRequest, TResponse>(request);
        var completion = completionObserver is null
            ? new ResponseCompletion<TRequest, TResponse>(entry)
            : new TracedResponseCompletion<TRequest, TResponse>(entry, completionObserver);
        var submission = batcher.ExecuteCoreAsync(entry, completion, cancellationToken, traceContext);
        return ReferenceEquals(submission, completion.Task)
            ? completion.Task
            : AwaitSubmissionAsync(submission, entry);
    }

    public Task<IReadOnlyList<TResponse>> ExecuteAsync(
        IEnumerable<TRequest> requests,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requests);
        // The call includes response-array assembly after queue completion, so observe the returned Task instead.
        return _callDiagnostics.Execute(
            RequestBatcherMetricTags.MultiSubmission,
            (Batcher: this, Requests: requests, Token: cancellationToken),
            static (state, context, _) => state.Batcher.ExecuteCoreAsync(state.Requests, state.Token, context),
            observeCompletion: false);
    }

    internal Task<IReadOnlyList<TResponse>> ExecuteCoreAsync(
        IEnumerable<TRequest> requests,
        CancellationToken cancellationToken,
        SubmissionTraceContext? traceContext = default)
    {
        ArgumentNullException.ThrowIfNull(requests);
        return ProcessEntriesAsync(requests, cancellationToken, traceContext);
    }

    private static async Task<TResponse> AwaitSubmissionAsync(
        Task submission,
        RequestBatchEntry<TRequest, TResponse> entry)
    {
        await submission.ConfigureAwait(false);
        return entry.GetResponse();
    }

    private async Task<IReadOnlyList<TResponse>> ProcessEntriesAsync(
        IEnumerable<TRequest> requests,
        CancellationToken cancellationToken,
        SubmissionTraceContext? traceContext)
    {
        var entries = new List<RequestBatchEntry<TRequest, TResponse>>();
        await batcher
            .ExecuteCoreAsync(CreateEntries(requests, entries), cancellationToken, traceContext)
            .ConfigureAwait(false);

        if (entries.Count == 0)
        {
            return Array.Empty<TResponse>();
        }

        var responses = new TResponse[entries.Count];
        for (var index = 0; index < entries.Count; index++)
        {
            responses[index] = entries[index].GetResponse();
        }

        return responses;
    }

    private static IEnumerable<RequestBatchEntry<TRequest, TResponse>> CreateEntries(
        IEnumerable<TRequest> requests,
        ICollection<RequestBatchEntry<TRequest, TResponse>> entries)
    {
        foreach (var request in requests)
        {
            var entry = new RequestBatchEntry<TRequest, TResponse>(request);
            entries.Add(entry);
            yield return entry;
        }
    }
}
