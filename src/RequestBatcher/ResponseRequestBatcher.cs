using System.Diagnostics;
using RequestBatcher.Diagnostics;
using RequestBatcher.PendingRequests;

namespace RequestBatcher;

internal sealed class ResponseRequestBatcher<TRequest, TResponse>(
    RequestBatchCoordinator<RequestBatchEntry<TRequest, TResponse>> batcher)
    : IRequestBatcher<TRequest, TResponse>
{
    private static readonly string _requestTypeName = typeof(TRequest).FullName ?? typeof(TRequest).Name;

    public Task<TResponse> ExecuteAsync(TRequest request, CancellationToken cancellationToken = default) =>
        RequestBatcherTracing.Source.HasListeners()
            ? RequestBatcherTracing.TraceCall(
                _requestTypeName,
                (Batcher: this, Request: request, Token: cancellationToken),
                static (state, context) => state.Batcher.ExecuteCoreAsync(state.Request, state.Token, context))
            : ExecuteCoreAsync(request, cancellationToken);

    internal Task<TResponse> ExecuteCoreAsync(
        TRequest request,
        CancellationToken cancellationToken,
        ActivityContext traceContext = default)
    {
        var entry = new RequestBatchEntry<TRequest, TResponse>(request);
        var completion = new ResponseCompletion<TRequest, TResponse>(entry);
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

        return RequestBatcherTracing.Source.HasListeners()
            ? RequestBatcherTracing.TraceCall(
                _requestTypeName,
                (Batcher: this, Requests: requests, Token: cancellationToken),
                static (state, context) => state.Batcher.ExecuteCoreAsync(state.Requests, state.Token, context))
            : ExecuteCoreAsync(requests, cancellationToken);
    }

    internal Task<IReadOnlyList<TResponse>> ExecuteCoreAsync(
        IEnumerable<TRequest> requests,
        CancellationToken cancellationToken,
        ActivityContext traceContext = default)
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
        ActivityContext traceContext)
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
