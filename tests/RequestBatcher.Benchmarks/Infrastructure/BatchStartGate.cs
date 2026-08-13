namespace RequestBatcher.Benchmarks.Infrastructure;

internal sealed class BatchStartGate
{
    private TaskCompletionSource _source = CreateSource();

    public Task WaitAsync() => Volatile.Read(ref _source).Task;

    public void Release() => Volatile.Read(ref _source).TrySetResult();

    public void Reset() => Volatile.Write(ref _source, CreateSource());

    private static TaskCompletionSource CreateSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
