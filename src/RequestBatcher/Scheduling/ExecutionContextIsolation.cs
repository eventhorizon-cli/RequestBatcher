namespace RequestBatcher.Scheduling;

/// <summary>
/// Runs asynchronous work without inheriting the caller's execution context, including during synchronous startup.
/// Used once per dispatch loop and monitor, not for individual requests or batches.
/// </summary>
internal static class ExecutionContextIsolation
{
    public static Task Run(Func<Task> action)
    {
        // Preserve suppression owned by the caller; do not suppress again or restore their flow.
        if (ExecutionContext.IsFlowSuppressed())
        {
            return Task.Run(action);
        }

        // Suppression prevents Task.Run from capturing Activity.Current and other AsyncLocal state.
        // Task.Run also isolates code before the action's first await, which suppression alone cannot do.
        // Restore caller flow synchronously after scheduling; never await inside this scope.
        using (ExecutionContext.SuppressFlow())
        {
            return Task.Run(action);
        }
    }
}
