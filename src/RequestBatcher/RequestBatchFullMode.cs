namespace RequestBatcher;

/// <summary>
/// Defines how a coordinator behaves when the maximum number of pending requests is reached.
/// </summary>
public enum RequestBatchFullMode
{
    /// <summary>Asynchronously wait until capacity becomes available.</summary>
    Wait,

    /// <summary>Fail the request submission immediately.</summary>
    Fail,
}
