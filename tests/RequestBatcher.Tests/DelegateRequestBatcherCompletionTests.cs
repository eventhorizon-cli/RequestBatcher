using System.Threading.Tasks.Sources;
using Microsoft.Extensions.DependencyInjection;

namespace RequestBatcher.Tests;

public sealed class DelegateRequestBatcherCompletionTests
{
    [Fact]
    public async Task ExecuteAsync_RequestOnlyTaskHandler_CompletesAfterReturnedTask()
    {
        var services = new ServiceCollection();
        services.AddDelegateRequestBatcher<TaskRequest>();
        await using var provider = services.BuildServiceProvider();
        var batcher = provider.GetRequiredService<IRequestBatcher<TaskRequest>>();
        var request = new TaskRequest();

        try
        {
            var completion = batcher.ExecuteAsync(request, CompleteAfterSignalAsync);

            await request.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(completion.IsCompleted);

            request.Completion.TrySetResult();
            await completion;
        }
        finally
        {
            request.Completion.TrySetResult();
        }
    }

    [Fact]
    public async Task ExecuteAsync_RequestOnlyValueTaskSource_CompletesAndReadsSourceOnce()
    {
        var services = new ServiceCollection();
        services.AddDelegateRequestBatcher<ValueTaskSourceRequest>();
        await using var provider = services.BuildServiceProvider();
        var batcher = provider.GetRequiredService<IRequestBatcher<ValueTaskSourceRequest>>();
        var request = new ValueTaskSourceRequest();

        try
        {
            var completion = batcher.ExecuteAsync(request, CompleteWithValueTaskSourceAsync);

            await request.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await request.Source.OnCompletedRegistered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(completion.IsCompleted);

            request.Source.Complete();
            await completion;

            Assert.Equal(1, request.Source.GetResultCount);
        }
        finally
        {
            request.Source.Complete();
        }
    }

    [Fact]
    public async Task ExecuteAsync_RequestOnlySynchronousFailure_PropagatesOriginalException()
    {
        var services = new ServiceCollection();
        services.AddDelegateRequestBatcher<FailureRequest>();
        await using var provider = services.BuildServiceProvider();
        var batcher = provider.GetRequiredService<IRequestBatcher<FailureRequest>>();
        var expected = new InvalidOperationException("synchronous failure");

        var completion = batcher.ExecuteAsync(new FailureRequest(expected), ThrowSynchronously);

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() => completion);
        Assert.Same(expected, actual);
    }

    [Fact]
    public async Task ExecuteAsync_RequestOnlyAsynchronousFailure_PropagatesOriginalException()
    {
        var services = new ServiceCollection();
        services.AddDelegateRequestBatcher<TaskRequest>();
        await using var provider = services.BuildServiceProvider();
        var batcher = provider.GetRequiredService<IRequestBatcher<TaskRequest>>();
        var request = new TaskRequest();
        var expected = new InvalidOperationException("asynchronous failure");

        try
        {
            var completion = batcher.ExecuteAsync(request, CompleteAfterSignalAsync);

            await request.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(completion.IsCompleted);
            request.Completion.TrySetException(expected);

            var actual = await Assert.ThrowsAsync<InvalidOperationException>(() => completion);
            Assert.Same(expected, actual);
        }
        finally
        {
            request.Completion.TrySetException(expected);
        }
    }

    [Fact]
    public async Task ExecuteAsync_RequestOnlyCanceledHandler_PropagatesCancellation()
    {
        var services = new ServiceCollection();
        services.AddDelegateRequestBatcher<CanceledRequest>();
        await using var provider = services.BuildServiceProvider();
        var batcher = provider.GetRequiredService<IRequestBatcher<CanceledRequest>>();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var completion = batcher.ExecuteAsync(
            new CanceledRequest(cancellation.Token),
            static (IReadOnlyList<CanceledRequest> requests) =>
                Task.FromCanceled(Assert.Single(requests).CancellationToken));

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => completion);
        Assert.Equal(cancellation.Token, exception.CancellationToken);
    }

    [Fact]
    public async Task ExecuteAsync_RequestOnlyConcurrencyAndDispose_DrainsAcceptedHandlers()
    {
        var services = new ServiceCollection();
        services.AddDelegateRequestBatcher<BlockingRequest>(options =>
        {
            options.BatchSize = 1;
            options.MaxConcurrency = 1;
        });
        await using var provider = services.BuildServiceProvider();
        var batcher = provider.GetRequiredService<IRequestBatcher<BlockingRequest>>();
        var probe = new HandlerProbe();
        var first = new BlockingRequest(probe);
        var second = new BlockingRequest(probe);

        try
        {
            var firstCompletion = batcher.ExecuteAsync(first, WaitForReleaseAsync);
            await first.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var secondCompletion = batcher.ExecuteAsync(second, WaitForReleaseAsync);
            Assert.False(second.Started.Task.IsCompleted);

            var disposal = provider.DisposeAsync().AsTask();
            Assert.False(disposal.IsCompleted);

            first.Release.TrySetResult();
            await firstCompletion;
            await second.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(disposal.IsCompleted);

            second.Release.TrySetResult();
            await secondCompletion;
            await disposal;
            Assert.Equal(1, Volatile.Read(ref probe.MaximumActiveHandlers));
        }
        finally
        {
            first.Release.TrySetResult();
            second.Release.TrySetResult();
        }
    }

    private static Task CompleteAfterSignalAsync(
        IReadOnlyList<TaskRequest> requests,
        CancellationToken cancellationToken)
    {
        Assert.True(cancellationToken.CanBeCanceled);
        var request = Assert.Single(requests);
        request.Started.TrySetResult();
        return request.Completion.Task;
    }

    private static ValueTask CompleteWithValueTaskSourceAsync(
        IReadOnlyList<ValueTaskSourceRequest> requests,
        CancellationToken cancellationToken)
    {
        Assert.True(cancellationToken.CanBeCanceled);
        var request = Assert.Single(requests);
        request.Started.TrySetResult();
        return request.Source.AsValueTask();
    }

    private static void ThrowSynchronously(IReadOnlyList<FailureRequest> requests) =>
        throw Assert.Single(requests).Exception;

    private static async Task WaitForReleaseAsync(
        IReadOnlyList<BlockingRequest> requests,
        CancellationToken cancellationToken)
    {
        Assert.True(cancellationToken.CanBeCanceled);
        var request = Assert.Single(requests);
        var activeHandlers = Interlocked.Increment(ref request.Probe.ActiveHandlers);
        UpdateMaximum(ref request.Probe.MaximumActiveHandlers, activeHandlers);
        request.Started.TrySetResult();
        try
        {
            await request.Release.Task.ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref request.Probe.ActiveHandlers);
        }
    }

    private sealed class TaskRequest
    {
        public TaskCompletionSource Started { get; } = NewSource();

        public TaskCompletionSource Completion { get; } = NewSource();
    }

    private sealed class ValueTaskSourceRequest
    {
        public TaskCompletionSource Started { get; } = NewSource();

        public CompletionSource Source { get; } = new();
    }

    private sealed class FailureRequest(InvalidOperationException exception)
    {
        public InvalidOperationException Exception { get; } = exception;
    }

    private sealed class CanceledRequest(CancellationToken cancellationToken)
    {
        public CancellationToken CancellationToken { get; } = cancellationToken;
    }

    private sealed class BlockingRequest(HandlerProbe probe)
    {
        public HandlerProbe Probe { get; } = probe;

        public TaskCompletionSource Started { get; } = NewSource();

        public TaskCompletionSource Release { get; } = NewSource();
    }

    private sealed class HandlerProbe
    {
        public int ActiveHandlers;

        public int MaximumActiveHandlers;
    }

    private sealed class CompletionSource : IValueTaskSource
    {
        private ManualResetValueTaskSourceCore<bool> _source;
        private int _completed;
        private int _getResultCount;

        public CompletionSource()
        {
            _source.RunContinuationsAsynchronously = true;
        }

        public int GetResultCount => Volatile.Read(ref _getResultCount);

        public TaskCompletionSource OnCompletedRegistered { get; } = NewSource();

        public ValueTask AsValueTask() => new(this, _source.Version);

        public void Complete()
        {
            if (Interlocked.Exchange(ref _completed, 1) == 0)
            {
                _source.SetResult(true);
            }
        }

        void IValueTaskSource.GetResult(short token)
        {
            Interlocked.Increment(ref _getResultCount);
            _source.GetResult(token);
        }

        ValueTaskSourceStatus IValueTaskSource.GetStatus(short token) => _source.GetStatus(token);

        void IValueTaskSource.OnCompleted(
            Action<object?> continuation,
            object? state,
            short token,
            ValueTaskSourceOnCompletedFlags flags)
        {
            _source.OnCompleted(continuation, state, token, flags);
            OnCompletedRegistered.TrySetResult();
        }
    }

    private static void UpdateMaximum(ref int maximum, int value)
    {
        while (true)
        {
            var current = Volatile.Read(ref maximum);
            if (current >= value || Interlocked.CompareExchange(ref maximum, value, current) == current)
            {
                return;
            }
        }
    }

    private static TaskCompletionSource NewSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
