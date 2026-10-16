namespace RequestBatcher;

/// <summary>
/// Provides response assignment helpers for handler batch entries.
/// </summary>
public static class RequestBatchEntryExtensions
{
    /// <summary>
    /// Sets one response on every request entry from an ordered response sequence.
    /// </summary>
    /// <typeparam name="TRequest">The request type.</typeparam>
    /// <typeparam name="TResponse">The response type.</typeparam>
    /// <param name="entries">The request entries that receive responses.</param>
    /// <param name="responses">The responses to assign.</param>
    /// <remarks>
    /// The first response is assigned to the first request entry, the second response to the second entry, and so on.
    /// The sequences must have exactly the same number of elements. <paramref name="responses"/> is enumerated once
    /// before any entry is modified, so an enumeration failure or count mismatch cannot leave a partially assigned
    /// batch.
    /// </remarks>
    /// <exception cref="ArgumentException">The response count does not match the request count.</exception>
    public static void SetResponses<TRequest, TResponse>(
        this IReadOnlyList<RequestBatchEntry<TRequest, TResponse>> entries,
        IEnumerable<TResponse> responses)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(responses);

        var responseArray = responses.ToArray();
        if (responseArray.Length != entries.Count)
        {
            throw new ArgumentException(
                "The response sequence must contain exactly one response for each request item.",
                nameof(responses));
        }

        EnsureResponsesAreUnset(entries);
        for (var index = 0; index < entries.Count; index++)
        {
            entries[index].SetResponse(responseArray[index]);
        }
    }

    /// <summary>
    /// Creates and sets one response for every request entry.
    /// </summary>
    /// <typeparam name="TRequest">The request type.</typeparam>
    /// <typeparam name="TResponse">The response type.</typeparam>
    /// <param name="entries">The request entries that receive responses.</param>
    /// <param name="responseFactory">Creates the response for each request.</param>
    /// <remarks>
    /// The factory is evaluated in request order. All responses are created before any entry is modified, so a factory
    /// failure cannot leave a partially assigned batch.
    /// </remarks>
    public static void SetResponses<TRequest, TResponse>(
        this IReadOnlyList<RequestBatchEntry<TRequest, TResponse>> entries,
        Func<TRequest, TResponse> responseFactory)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(responseFactory);

        var responses = new TResponse[entries.Count];
        for (var index = 0; index < entries.Count; index++)
        {
            responses[index] = responseFactory(entries[index].Request);
        }

        entries.SetResponses(responses);
    }

    private static void EnsureResponsesAreUnset<TRequest, TResponse>(
        IReadOnlyList<RequestBatchEntry<TRequest, TResponse>> entries)
    {
        for (var index = 0; index < entries.Count; index++)
        {
            entries[index].EnsureResponseIsUnset();
        }
    }
}
