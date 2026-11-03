namespace RequestBatcher;

/// <summary>
/// Represents one request and its response slot within a response-bearing handler batch.
/// </summary>
/// <typeparam name="TRequest">The request type.</typeparam>
/// <typeparam name="TResponse">The response type.</typeparam>
/// <remarks>
/// Each entry has one writer. Await all response-producing work before the handler returns.
/// </remarks>
public sealed class RequestBatchEntry<TRequest, TResponse>
{
    private TResponse _response = default!;
    private bool _hasResponse;

    internal RequestBatchEntry(TRequest request) => Request = request;

    /// <summary>
    /// Gets the request submitted by the caller.
    /// </summary>
    public TRequest Request { get; }

    /// <summary>
    /// Sets this request's response.
    /// </summary>
    /// <param name="response">The response for <see cref="Request"/>. A null response is valid.</param>
    /// <remarks>
    /// A response must be assigned exactly once before the handler returns successfully. Use
    /// <see cref="RequestBatchEntryExtensions.SetResponses{TRequest, TResponse}(IReadOnlyList{RequestBatchEntry{TRequest, TResponse}}, IEnumerable{TResponse})"/>
    /// when responses are available as an ordered sequence for the whole batch.
    /// </remarks>
    /// <exception cref="InvalidOperationException">A response was already assigned to this entry.</exception>
    public void SetResponse(TResponse response)
    {
        if (_hasResponse)
        {
            throw new InvalidOperationException("A response has already been assigned to this batch entry.");
        }

        _response = response;
        // Handler completion and request completion publish these single-writer values to their readers.
        _hasResponse = true;
    }

    internal TResponse GetResponse()
    {
        EnsureResponseIsSet();
        return _response;
    }

    internal void EnsureResponseIsSet()
    {
        if (!_hasResponse)
        {
            throw new InvalidOperationException(
                "The response handler returned without assigning a response to this batch entry.");
        }
    }
}
