namespace RequestBatcher.Diagnostics;

internal static class RequestBatcherRequestType
{
    internal static string GetName(Type requestType)
    {
        ArgumentNullException.ThrowIfNull(requestType);

        if (requestType.IsGenericType &&
            requestType.GetGenericTypeDefinition() == typeof(RequestBatchEntry<,>))
        {
            requestType = requestType.GetGenericArguments()[0];
        }

        return requestType.Name;
    }
}
