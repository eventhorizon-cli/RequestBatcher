namespace RequestBatcher.Scheduling;

/// <summary>
/// Isolates background startup and restores ambient context around synchronous callbacks.
/// </summary>
internal static class ExecutionContextIsolation
{
    // Only the dispatch loop and monitor use this scheduling entry, once per batcher.
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

    /// <summary>
    /// Runs a callback synchronously and restores the invoking thread's ambient state before returning or rethrowing.
    /// </summary>
    /// <param name="capturedContext">
    /// The context captured for the callback. If null, use the invoking thread's current values while still restoring
    /// callback changes. This preserves the absence of a submitted context when its capture was suppressed.
    /// </param>
    /// <param name="action">The synchronous callback to invoke.</param>
    /// <param name="state">The state passed to the callback.</param>
    public static void RunSynchronously(
        ExecutionContext? capturedContext,
        ContextCallback action,
        object? state)
    {
        capturedContext ??= ExecutionContext.Capture();
        if (capturedContext is not null)
        {
            ExecutionContext.Run(capturedContext, action, state);
            return;
        }

        RunWithSuppressedFlow(action, state);
    }

    private static void RunWithSuppressedFlow(ContextCallback action, object? state)
    {
        // Capture returns null under suppression. Snapshot these values, then restore the caller's
        // suppression before invoking any callback so Run also restores it on exceptional exits.
        ExecutionContext context;
        ExecutionContext.RestoreFlow();
        try
        {
            context = ExecutionContext.Capture()!;
        }
        finally
        {
            ExecutionContext.SuppressFlow();
        }

        ExecutionContext.Run(
            context,
            static value => ((SuppressedCallback)value!).Invoke(),
            new SuppressedCallback(action, state));
    }

    private sealed class SuppressedCallback(ContextCallback action, object? state)
    {
        public void Invoke()
        {
            // The snapshot has flow enabled. Keep callback-started work subject to the caller's suppression.
            using (ExecutionContext.SuppressFlow())
            {
                action(state);
            }
        }
    }
}
