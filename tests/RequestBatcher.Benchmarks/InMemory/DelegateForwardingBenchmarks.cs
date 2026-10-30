using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using Microsoft.Extensions.DependencyInjection;
using RequestBatcher.Handlers.Delegate;

namespace RequestBatcher.Benchmarks.InMemory;

/// <summary>
/// Isolates no-service delegate forwarding from a typed request handler with identical asynchronous work.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(
    RunStrategy.Throughput,
    launchCount: 1,
    warmupCount: 3,
    iterationCount: 8,
    id: "DelegateForwarding")]
[IterationTime(300)]
public class DelegateForwardingBenchmarks
{
    private static readonly Func<IReadOnlyList<ForwardingRequest>, CancellationToken, ValueTask> _staticHandler =
        AwaitReleaseAsync;

    private ServiceProvider _provider = null!;
    private IRequestBatchHandler<ForwardingRequest> _typedHandler = null!;
    private DelegateRequestBatchHandler<ForwardingRequest> _delegateHandler = null!;
    private ForwardingRequest[] _requests = null!;
    private ForwardingState _state = null!;

    /// <summary>Builds both handlers and binds the delegate outside measurement.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _state = new ForwardingState();
        _requests = [new ForwardingRequest(_state)];
        _typedHandler = new TypedRequestHandler();

        _provider = new ServiceCollection().BuildServiceProvider();
        _delegateHandler = new DelegateRequestBatchHandler<ForwardingRequest>(
            _provider.GetRequiredService<IServiceScopeFactory>());
        _delegateHandler.Bind(_staticHandler);

        RunTypedAsync().GetAwaiter().GetResult();
        RunDelegateAsync().GetAwaiter().GetResult();
    }

    /// <summary>Measures typed-handler invocation with the shared asynchronous gate.</summary>
    [Benchmark(Baseline = true)]
    public Task TypedHandler() => RunTypedAsync();

    /// <summary>Measures bound delegate forwarding with the same asynchronous gate.</summary>
    [Benchmark]
    public Task DelegateHandler() => RunDelegateAsync();

    /// <summary>Disposes the setup provider outside measurement.</summary>
    [GlobalCleanup]
    public async Task CleanupAsync() => await _provider.DisposeAsync().ConfigureAwait(false);

    private async Task RunTypedAsync()
    {
        var release = _state.Begin();
        var pending = _typedHandler.HandleAsync(_requests, CancellationToken.None);
        release.SetResult();
        await pending.ConfigureAwait(false);
        _state.Validate();
    }

    private async Task RunDelegateAsync()
    {
        var release = _state.Begin();
        var pending = _delegateHandler.HandleAsync(_requests, CancellationToken.None);
        release.SetResult();
        await pending.ConfigureAwait(false);
        _state.Validate();
    }

    private static ValueTask AwaitReleaseAsync(
        IReadOnlyList<ForwardingRequest> requests,
        CancellationToken cancellationToken) => requests[0].State.WaitAsync(cancellationToken);

    private sealed class TypedRequestHandler : IRequestBatchHandler<ForwardingRequest>
    {
        public ValueTask HandleAsync(
            IReadOnlyList<ForwardingRequest> requests,
            CancellationToken cancellationToken = default) => AwaitReleaseAsync(requests, cancellationToken);
    }

    private sealed class ForwardingRequest(ForwardingState state)
    {
        public ForwardingState State { get; } = state;
    }

    private sealed class ForwardingState
    {
        private TaskCompletionSource? _release;
        private int _completed;

        public TaskCompletionSource Begin()
        {
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref _completed, 0);
            Volatile.Write(ref _release, release);
            return release;
        }

        public async ValueTask WaitAsync(CancellationToken cancellationToken)
        {
            var release = Volatile.Read(ref _release) ??
                throw new InvalidOperationException("The forwarding benchmark was not initialized.");
            await release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            Interlocked.Increment(ref _completed);
        }

        public void Validate()
        {
            var completed = Volatile.Read(ref _completed);
            if (completed != 1)
            {
                throw new InvalidOperationException(
                    $"The forwarding handler completed {completed} times; expected exactly once.");
            }
        }
    }
}
