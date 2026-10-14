using RequestBatcher.PendingRequests;

namespace RequestBatcher;

internal sealed class ResponseRequestBatcher<TRequest, TResponse>(
    RequestBatchCoordinator<RequestBatchItem<TRequest, TResponse>> batcher)
    : IRequestBatcher<TRequest, TResponse>
{
    public Task<TResponse> ExecuteAsync(TRequest request, CancellationToken cancellationToken = default)
    {
        var entry = new RequestBatchItem<TRequest, TResponse>(request);
        var completion = new ResponseCompletion<TRequest, TResponse>(entry);
        var submission = batcher.ExecuteAsync(entry, completion, cancellationToken);
        return ReferenceEquals(submission, completion.Task)
            ? completion.Task
            : AwaitSubmissionAsync(submission, entry);
    }

    public Task<IReadOnlyList<TResponse>> ExecuteAsync(
        IEnumerable<TRequest> requests,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requests);

        return ProcessEntriesAsync(requests, cancellationToken);
    }

    private static async Task<TResponse> AwaitSubmissionAsync(
        Task submission,
        RequestBatchItem<TRequest, TResponse> entry)
    {
        await submission.ConfigureAwait(false);
        return entry.GetResponse();
    }

    private async Task<IReadOnlyList<TResponse>> ProcessEntriesAsync(
        IEnumerable<TRequest> requests,
        CancellationToken cancellationToken)
    {
        var entries = new List<RequestBatchItem<TRequest, TResponse>>();
        await batcher
            .ExecuteAsync(CreateEntries(requests, entries), cancellationToken)
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

    private static IEnumerable<RequestBatchItem<TRequest, TResponse>> CreateEntries(
        IEnumerable<TRequest> requests,
        ICollection<RequestBatchItem<TRequest, TResponse>> entries)
    {
        foreach (var request in requests)
        {
            var entry = new RequestBatchItem<TRequest, TResponse>(request);
            entries.Add(entry);
            yield return entry;
        }
    }
}
