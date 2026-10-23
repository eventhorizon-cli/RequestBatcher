using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Engines;
using BenchmarkDotNet.Order;

namespace RequestBatcher.Benchmarks.InMemory;

/// <summary>
/// Compares pending tracing completion bridges without running the request queue.
/// </summary>
[MemoryDiagnoser]
[RankColumn]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
[SimpleJob(
    RunStrategy.Throughput,
    launchCount: 2,
    warmupCount: 4,
    iterationCount: 12,
    invocationCount: 8,
    id: "TracingCompletion")]
public class TracingCompletionBenchmarks
{
    private const int RequestCount = 1_024;
    private const string CapturedAmbientValue = "captured";
    private const string ChangedAmbientValue = "changed";
    private readonly CompletionProbe _probe = new();

    /// <summary>
    /// Compares the existing asynchronous Task helper with a completion source.
    /// </summary>
    [Benchmark(Baseline = true, Description = "Async Task helper + TCS")]
    public Task AsyncTaskHelperAsync() => RunAsync(StartWithTaskHelper);

    /// <summary>
    /// Compares an ordinary asynchronous ValueTask helper with a completion source.
    /// </summary>
    [Benchmark(Description = "Async ValueTask helper + TCS")]
    public Task AsyncValueTaskHelperAsync() => RunAsync(StartWithValueTaskHelper);

    /// <summary>
    /// Compares an awaiter callback object that owns the completion source.
    /// </summary>
    [Benchmark(Description = "Awaiter callback object + TCS")]
    public Task AwaiterCallbackAsync() => RunAsync(StartWithCallback);

    private async Task RunAsync(Func<Task, Action<Task>, Task> start)
    {
        _probe.Reset();
        var sources = new TaskCompletionSource[RequestCount];
        var wrappers = new Task[RequestCount];

        for (var index = 0; index < RequestCount; index++)
        {
            var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            sources[index] = source;
            _probe.Ambient.Value = CapturedAmbientValue;
            wrappers[index] = start(source.Task, _probe.Callback);
            _probe.Ambient.Value = ChangedAmbientValue;
        }

        if (sources.Any(static source => source.Task.IsCompleted))
        {
            throw new InvalidOperationException("A source completed before every wrapper was registered.");
        }

        for (var index = 0; index < sources.Length; index++)
        {
            sources[index].TrySetResult();
        }

        await Task.WhenAll(wrappers).ConfigureAwait(false);
        _probe.Validate(RequestCount);
        _probe.Ambient.Value = null;
    }

    private static Task StartWithTaskHelper(Task source, Action<Task> completionAction)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = CompleteWithTaskAsync(source, completion, completionAction);
        return completion.Task;
    }

    private static async Task CompleteWithTaskAsync(
        Task source,
        TaskCompletionSource completion,
        Action<Task> completionAction)
    {
        await source.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        completionAction(source);
        TransferCompletion(source, completion);
    }

    private static Task StartWithValueTaskHelper(Task source, Action<Task> completionAction)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = CompleteWithValueTaskAsync(source, completion, completionAction);
        return completion.Task;
    }

    private static async ValueTask CompleteWithValueTaskAsync(
        Task source,
        TaskCompletionSource completion,
        Action<Task> completionAction)
    {
        await source.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        completionAction(source);
        TransferCompletion(source, completion);
    }

    private static Task StartWithCallback(Task source, Action<Task> completionAction)
    {
        var completion = new CallbackCompletion(source, completionAction);
        completion.Register();
        return completion.Task;
    }

    private static void TransferCompletion(Task source, TaskCompletionSource completion)
    {
        if (source.IsFaulted)
        {
            completion.TrySetException(source.Exception!.InnerExceptions);
        }
        else if (source.IsCanceled)
        {
            completion.TrySetCanceled(GetCancellationToken(source));
        }
        else
        {
            completion.TrySetResult();
        }
    }

    private static CancellationToken GetCancellationToken(Task task)
    {
        try
        {
            task.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException exception)
        {
            return exception.CancellationToken;
        }

        return default;
    }

    private sealed class CallbackCompletion :
        TaskCompletionSource
    {
        private static readonly ContextCallback _completeWithContextCallback = static state =>
            ((CallbackCompletion)state!).CompleteUnderCapturedContext();
        private readonly ExecutionContext? _capturedContext;
        private readonly Action _completion;

        private readonly Task _source;
        private readonly Action<Task> _completionAction;

        public CallbackCompletion(Task source, Action<Task> completionAction)
            : base(TaskCreationOptions.RunContinuationsAsynchronously)
        {
            _source = source;
            _completionAction = completionAction;
            _capturedContext = ExecutionContext.Capture();
            _completion = Complete;
        }

        public void Register()
        {
            _source.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing)
                .GetAwaiter()
                .UnsafeOnCompleted(_completion);
        }

        private void Complete()
        {
            if (_capturedContext is null)
            {
                CompleteUnderCapturedContext();
                return;
            }

            ExecutionContext.Run(_capturedContext, _completeWithContextCallback, this);
        }

        private void CompleteUnderCapturedContext()
        {
            var source = _source;
            _completionAction(source);
            TransferCompletion(source, this);
        }
    }

    private sealed class CompletionProbe
    {
        private int _callbackCount;
        private string? _error;

        public CompletionProbe()
        {
            Callback = OnCompleted;
        }

        public AsyncLocal<string?> Ambient { get; } = new();

        public Action<Task> Callback { get; }

        public void Reset()
        {
            _callbackCount = 0;
            _error = null;
        }

        public void Validate(int expectedCount)
        {
            if (_error is not null)
            {
                throw new InvalidOperationException(_error);
            }

            if (_callbackCount != expectedCount)
            {
                throw new InvalidOperationException(
                    $"Expected {expectedCount} completion callbacks, observed {_callbackCount}.");
            }
        }

        private void OnCompleted(Task source)
        {
            if (!source.IsCompletedSuccessfully)
            {
                Interlocked.CompareExchange(ref _error, "The source task did not complete successfully.", null);
            }
            else if (Ambient.Value != CapturedAmbientValue)
            {
                Interlocked.CompareExchange(
                    ref _error,
                    $"Expected ambient value '{CapturedAmbientValue}', observed '{Ambient.Value ?? "<null>"}'.",
                    null);
            }

            Interlocked.Increment(ref _callbackCount);
        }
    }
}
