namespace RequestBatcher;

/// <summary>
/// Provides response assignment helpers for handler batch items.
/// </summary>
public static class RequestBatchItemExtensions
{
    /// <summary>
    /// Sets one response on every request item from an ordered response sequence.
    /// </summary>
    /// <typeparam name="TRequest">The request type.</typeparam>
    /// <typeparam name="TResponse">The response type.</typeparam>
    /// <param name="requests">The request items that receive responses.</param>
    /// <param name="responses">The responses to assign.</param>
    /// <remarks>
    /// The first response is assigned to the first request item, the second response to the second item, and so on.
    /// The sequences must have exactly the same number of elements. <paramref name="responses"/> is enumerated once
    /// before any item is modified, so an enumeration failure or count mismatch cannot leave a partially assigned
    /// batch.
    /// </remarks>
    /// <exception cref="ArgumentException">The response count does not match the request count.</exception>
    public static void SetResponses<TRequest, TResponse>(
        this IReadOnlyList<RequestBatchItem<TRequest, TResponse>> requests,
        IEnumerable<TResponse> responses)
    {
        ArgumentNullException.ThrowIfNull(requests);
        ArgumentNullException.ThrowIfNull(responses);

        var responseArray = responses.ToArray();
        if (responseArray.Length != requests.Count)
        {
            throw new ArgumentException(
                "The response sequence must contain exactly one response for each request item.",
                nameof(responses));
        }

        EnsureResponsesAreUnset(requests);
        for (var index = 0; index < requests.Count; index++)
        {
            requests[index].SetResponse(responseArray[index]);
        }
    }

    /// <summary>
    /// Creates and sets one response for every request item.
    /// </summary>
    /// <typeparam name="TRequest">The request type.</typeparam>
    /// <typeparam name="TResponse">The response type.</typeparam>
    /// <param name="requests">The request items that receive responses.</param>
    /// <param name="responseFactory">Creates the response for each request.</param>
    /// <remarks>
    /// The factory is evaluated in request order. All responses are created before any item is modified, so a factory
    /// failure cannot leave a partially assigned batch.
    /// </remarks>
    public static void SetResponses<TRequest, TResponse>(
        this IReadOnlyList<RequestBatchItem<TRequest, TResponse>> requests,
        Func<TRequest, TResponse> responseFactory)
    {
        ArgumentNullException.ThrowIfNull(requests);
        ArgumentNullException.ThrowIfNull(responseFactory);

        var responses = new TResponse[requests.Count];
        for (var index = 0; index < requests.Count; index++)
        {
            responses[index] = responseFactory(requests[index].Request);
        }

        requests.SetResponses(responses);
    }

    private static void EnsureResponsesAreUnset<TRequest, TResponse>(
        IReadOnlyList<RequestBatchItem<TRequest, TResponse>> requests)
    {
        for (var index = 0; index < requests.Count; index++)
        {
            requests[index].EnsureResponseIsUnset();
        }
    }
}
