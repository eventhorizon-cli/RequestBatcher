namespace RequestBatcher;

/// <summary>
/// Represents one request and its response slot within a response-bearing handler batch.
/// </summary>
/// <typeparam name="TRequest">The request type.</typeparam>
/// <typeparam name="TResponse">The response type.</typeparam>
public sealed class RequestBatchItem<TRequest, TResponse>
{
    private const int ResponseUnset = 0;
    private const int ResponseSetting = 1;
    private const int ResponseSet = 2;

    private TResponse _response = default!;
    private int _responseState;

    internal RequestBatchItem(TRequest request) => Request = request;

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
    /// <see cref="RequestBatchItemExtensions.SetResponses{TRequest, TResponse}(IReadOnlyList{RequestBatchItem{TRequest, TResponse}}, IEnumerable{TResponse})"/>
    /// when responses are available as an ordered sequence for the whole batch.
    /// </remarks>
    /// <exception cref="InvalidOperationException">A response was already assigned to this item.</exception>
    public void SetResponse(TResponse response)
    {
        if (Interlocked.CompareExchange(ref _responseState, ResponseSetting, ResponseUnset) != ResponseUnset)
        {
            throw new InvalidOperationException("A response has already been assigned to this batch item.");
        }

        _response = response;
        Volatile.Write(ref _responseState, ResponseSet);
    }

    internal TResponse GetResponse()
    {
        if (Volatile.Read(ref _responseState) != ResponseSet)
        {
            throw new InvalidOperationException(
                "The response handler returned without assigning a response to this batch item.");
        }

        return _response;
    }

    internal void EnsureResponseIsUnset()
    {
        if (Volatile.Read(ref _responseState) != ResponseUnset)
        {
            throw new InvalidOperationException("A response has already been assigned to this batch item.");
        }
    }
}
