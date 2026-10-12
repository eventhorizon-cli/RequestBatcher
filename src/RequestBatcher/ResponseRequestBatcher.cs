using RequestBatcher.PendingRequests;

namespace RequestBatcher;

internal sealed class ResponseRequestBatcher<TRequest, TResponse>(
    RequestBatchCoordinator<RequestBatchItem<TRequest, TResponse>> batcher)
    : IRequestBatcher<TRequest, TResponse>
{
    public Task<TResponse> ProcessAsync(TRequest request, CancellationToken cancellationToken = default)
    {
        var item = new RequestBatchItem<TRequest, TResponse>(request);
        var completion = new ResponseCompletion<TRequest, TResponse>(item);
        var submission = batcher.ProcessAsync(item, completion, cancellationToken);
        return ReferenceEquals(submission, completion.Task)
            ? completion.Task
            : AwaitSubmissionAsync(submission, item);
    }

    public Task<IReadOnlyList<TResponse>> ProcessAsync(
        IEnumerable<TRequest> requests,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requests);

        return ProcessItemsAsync(requests, cancellationToken);
    }

    private static async Task<TResponse> AwaitSubmissionAsync(
        Task submission,
        RequestBatchItem<TRequest, TResponse> item)
    {
        await submission.ConfigureAwait(false);
        return item.GetResponse();
    }

    private async Task<IReadOnlyList<TResponse>> ProcessItemsAsync(
        IEnumerable<TRequest> requests,
        CancellationToken cancellationToken)
    {
        var items = new List<RequestBatchItem<TRequest, TResponse>>();
        await batcher
            .ProcessAsync(CreateItems(requests, items), cancellationToken)
            .ConfigureAwait(false);

        if (items.Count == 0)
        {
            return Array.Empty<TResponse>();
        }

        var responses = new TResponse[items.Count];
        for (var index = 0; index < items.Count; index++)
        {
            responses[index] = items[index].GetResponse();
        }

        return responses;
    }

    private static IEnumerable<RequestBatchItem<TRequest, TResponse>> CreateItems(
        IEnumerable<TRequest> requests,
        ICollection<RequestBatchItem<TRequest, TResponse>> items)
    {
        foreach (var request in requests)
        {
            var item = new RequestBatchItem<TRequest, TResponse>(request);
            items.Add(item);
            yield return item;
        }
    }
}
