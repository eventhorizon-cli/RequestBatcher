using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace RequestBatcher.Tests;

public sealed class DelegateRequestBatcherTests
{
    [Fact]
    public async Task AddDelegateRequestBatcher_DefaultRequest_ExecutesStaticHandler()
    {
        var services = new ServiceCollection();
        services.AddDelegateRequestBatcher<int>();
        await using var provider = services.BuildServiceProvider();
        var batcher = provider.GetRequiredService<IRequestBatcher<int>>();

        await batcher.ExecuteAsync(42, static (IReadOnlyList<int> requests) =>
        {
            Assert.Equal([42], requests);
        });

    }

    [Fact]
    public async Task AddDelegateRequestBatcher_ResponseList_ReturnsInputOrderAndMissingValues()
    {
        var services = new ServiceCollection();
        services.AddDelegateRequestBatcher<int, string?>();
        await using var provider = services.BuildServiceProvider();
        var batcher = provider.GetRequiredService<IRequestBatcher<int, string?>>();

        var actual = await batcher.ExecuteAsync(
            [3, 1, 3, 9],
            static (IReadOnlyList<RequestBatchEntry<int, string?>> entries) =>
                entries.SetResponses(static request => request switch
                {
                    1 => "one",
                    3 => "three",
                    _ => null
                }));

        Assert.Equal(["three", "one", "three", null], actual);
    }

    [Fact]
    public async Task AddDelegateRequestBatcher_NamedResponse_ResolvesKeyedBatcherAndOptions()
    {
        var services = new ServiceCollection();
        services.AddDelegateRequestBatcher<int, decimal?>(
            "price",
            options =>
            {
                options.BatchSize = 3;
                options.MaxConcurrency = 2;
            });
        await using var provider = services.BuildServiceProvider();
        var batcher = provider.GetRequiredKeyedService<IRequestBatcher<int, decimal?>>("price");

        var actual = await batcher.ExecuteAsync(
            [101, 102, 101],
            static (IReadOnlyList<RequestBatchEntry<int, decimal?>> entries) =>
                entries.SetResponses(static request => request == 101 ? 17.90m : null));

        Assert.Equal([17.90m, null, 17.90m], actual);
        Assert.Equal(
            3,
            provider.GetRequiredService<IOptionsMonitor<RequestBatchOptions<int>>>().Get("price").BatchSize);
    }

    [Fact]
    public async Task AddDelegateRequestBatcher_DefaultAndNamed_KeepPipelinesIndependent()
    {
        var services = new ServiceCollection();
        services.AddDelegateRequestBatcher<int, string>();
        services.AddDelegateRequestBatcher<int, string>("price");
        await using var provider = services.BuildServiceProvider();

        var unnamed = provider.GetRequiredService<IRequestBatcher<int, string>>();
        var named = provider.GetRequiredKeyedService<IRequestBatcher<int, string>>("price");

        var unnamedResult = await unnamed.ExecuteAsync(
            1,
            static (IReadOnlyList<RequestBatchEntry<int, string>> entries) =>
                entries.SetResponses(static _ => "default"));
        var namedResult = await named.ExecuteAsync(
            1,
            static (IReadOnlyList<RequestBatchEntry<int, string>> entries) =>
                entries.SetResponses(static _ => "named"));

        Assert.Equal("default", unnamedResult);
        Assert.Equal("named", namedResult);
    }

    [Fact]
    public void AddDelegateRequestBatcher_DuplicateName_LeavesServicesUnchanged()
    {
        var services = new ServiceCollection();
        services.AddDelegateRequestBatcher<int>("price");
        var descriptorCount = services.Count;

        var exception = Assert.Throws<InvalidOperationException>(
            () => services.AddDelegateRequestBatcher<int>("price"));

        Assert.Contains("already", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(descriptorCount, services.Count);
    }

    [Fact]
    public async Task AddDelegateRequestBatcher_ResponseOptions_ProjectPartitionKey()
    {
        var routing = new RoutingProbe();
        var services = new ServiceCollection();
        services.AddSingleton(routing);
        services.AddDelegateRequestBatcher<int, int>(
            options =>
            {
                options.BatchSize = 100;
                options.UsePartitionKey(static request => request % 2, partitionCount: 2);
            });
        await using var provider = services.BuildServiceProvider();
        var batcher = provider.GetRequiredService<IRequestBatcher<int, int>>();

        var actual = await batcher.ExecuteAsync(
            [3, 1, 2, 4],
            static (IReadOnlyList<RequestBatchEntry<int, int>> entries, RoutingProbe probe) =>
            {
                probe.Batches.Enqueue(entries.Select(static entry => entry.Request).ToArray());
                entries.SetResponses(static request => request * 10);
            });

        Assert.Equal([30, 10, 20, 40], actual);
        Assert.NotEmpty(routing.Batches);
        Assert.All(
            routing.Batches,
            static batch => Assert.True(batch.Select(static request => request % 2).Distinct().Count() <= 1));
        Assert.Equal([1, 2, 3, 4], routing.Batches.SelectMany(static batch => batch).Order().ToArray());
    }

    [Fact]
    public async Task ExecuteAsync_InlineRegistrationWithoutHandler_Throws()
    {
        var services = new ServiceCollection();
        services.AddDelegateRequestBatcher<int>();
        await using var provider = services.BuildServiceProvider();
        var batcher = provider.GetRequiredService<IRequestBatcher<int>>();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => batcher.ExecuteAsync(1));

        Assert.Contains("delegate handler", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_TypedRegistration_RejectsInlineHandler()
    {
        var services = new ServiceCollection();
        services.AddRequestBatcher<int, NoOpTypedHandler>(ServiceLifetime.Singleton);
        await using var provider = services.BuildServiceProvider();
        var batcher = provider.GetRequiredService<IRequestBatcher<int>>();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => batcher.ExecuteAsync(1, static (IReadOnlyList<int> _) => { }));

        Assert.Contains("AddDelegateRequestBatcher", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_ThirdPartyBatcher_RejectsInlineHandler()
    {
        var batcher = new ThirdPartyBatcher();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => batcher.ExecuteAsync(1, static (IReadOnlyList<int> _) => { }));

        Assert.Contains("AddDelegateRequestBatcher", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_DifferentDelegate_RejectsBeforeAdmission()
    {
        var services = new ServiceCollection();
        services.AddDelegateRequestBatcher<int>();
        await using var provider = services.BuildServiceProvider();
        var batcher = provider.GetRequiredService<IRequestBatcher<int>>();

        await batcher.ExecuteAsync(1, static (IReadOnlyList<int> _) => { });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => batcher.ExecuteAsync(2, static (IReadOnlyList<int> _) => { }));

        Assert.Contains("different delegate", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_InvalidSignature_DoesNotBind()
    {
        var services = new ServiceCollection();
        services.AddDelegateRequestBatcher<int>();
        await using var provider = services.BuildServiceProvider();
        var batcher = provider.GetRequiredService<IRequestBatcher<int>>();

        var invalid = (Delegate)(Action<int, int>)(static (int _, int _) => { });
        var exception = await Assert.ThrowsAnyAsync<Exception>(
            () => batcher.ExecuteAsync(1, invalid));

        Assert.Contains("parameter", exception.Message, StringComparison.OrdinalIgnoreCase);

        await batcher.ExecuteAsync(2, static (IReadOnlyList<int> _) => { });
    }

    [Fact]
    public async Task ExecuteAsync_ConcurrentFirstBinding_ReusesHandler()
    {
        var services = new ServiceCollection();
        services.AddDelegateRequestBatcher<int>();
        await using var provider = services.BuildServiceProvider();
        var batcher = provider.GetRequiredService<IRequestBatcher<int>>();

        var tasks = Enumerable.Range(1, 32)
            .Select(value => batcher.ExecuteAsync(
                value,
                static (IReadOnlyList<int> _) => { }))
            .ToArray();

        await Task.WhenAll(tasks);
    }

    [Fact]
    public async Task ExecuteAsync_KeyedService_ResolvesFromBatchScope()
    {
        var services = new ServiceCollection();
        services.AddKeyedSingleton<Multiplier>("double", new Multiplier(2));
        services.AddDelegateRequestBatcher<int, int>();
        await using var provider = services.BuildServiceProvider();
        var batcher = provider.GetRequiredService<IRequestBatcher<int, int>>();

        var actual = await batcher.ExecuteAsync(
            21,
            static (
                IReadOnlyList<RequestBatchEntry<int, int>> entries,
                [FromKeyedServices("double")] Multiplier multiplier) =>
                entries.SetResponses(request => request * multiplier.Value));

        Assert.Equal(42, actual);
    }

    [Fact]
    public async Task ExecuteAsync_InheritKeyedService_RejectsWithoutBinding()
    {
        var services = new ServiceCollection();
        services.AddKeyedSingleton<Multiplier>("double", new Multiplier(2));
        services.AddDelegateRequestBatcher<int>();
        await using var provider = services.BuildServiceProvider();
        var batcher = provider.GetRequiredService<IRequestBatcher<int>>();

        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => batcher.ExecuteAsync(
                21,
                static (
                    IReadOnlyList<int> _,
                    [FromKeyedServices] Multiplier _) =>
                { }));

        Assert.Contains("explicit key", exception.Message, StringComparison.OrdinalIgnoreCase);

        await batcher.ExecuteAsync(
            21,
            static (IReadOnlyList<int> _, [FromKeyedServices("double")] Multiplier multiplier) =>
            {
                Assert.Equal(2, multiplier.Value);
            });
    }

    [Fact]
    public async Task ExecuteAsync_ScopedService_DisposesBeforeCompletion()
    {
        var state = new ScopeState();
        var services = new ServiceCollection();
        services.AddSingleton(state);
        services.AddScoped<ScopedDependency>();
        services.AddDelegateRequestBatcher<int>();
        await using var provider = services.BuildServiceProvider();
        var batcher = provider.GetRequiredService<IRequestBatcher<int>>();

        await batcher.ExecuteAsync(
            1,
            static (IReadOnlyList<int> _, ScopedDependency dependency) =>
            {
                dependency.State.Invocations++;
            });

        Assert.Equal(1, state.Invocations);
        Assert.Equal(1, state.Disposals);
    }

    [Fact]
    public async Task ExecuteAsync_FailingHandler_PropagatesOriginalException()
    {
        var expected = new DelegateFailureException();
        DelegateFailureState.Exception = expected;

        var services = new ServiceCollection();
        services.AddDelegateRequestBatcher<int>();
        await using var provider = services.BuildServiceProvider();
        var batcher = provider.GetRequiredService<IRequestBatcher<int>>();

        var actual = await Assert.ThrowsAsync<DelegateFailureException>(
            () => batcher.ExecuteAsync(
                1,
                (Delegate)(Action<IReadOnlyList<int>>)(static (IReadOnlyList<int> _) =>
                    throw DelegateFailureState.Exception)));

        Assert.Same(expected, actual);
    }

    [Fact]
    public async Task ExecuteAsync_CanceledBeforeDispatch_DoesNotBindHandler()
    {
        var services = new ServiceCollection();
        services.AddDelegateRequestBatcher<int>();
        await using var provider = services.BuildServiceProvider();
        var batcher = provider.GetRequiredService<IRequestBatcher<int>>();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => batcher.ExecuteAsync(
                1,
                static (IReadOnlyList<int> _) => { },
                cancellation.Token));

        await batcher.ExecuteAsync(2, static (IReadOnlyList<int> _) => { });
    }

    [Fact]
    public async Task ExecuteAsync_CanceledAfterDispatch_CompletesWithHandlerOutcome()
    {
        var probe = new BlockingProbe();
        BlockingProbe.Current = probe;
        var services = new ServiceCollection();
        services.AddDelegateRequestBatcher<int>();
        await using var provider = services.BuildServiceProvider();
        var batcher = provider.GetRequiredService<IRequestBatcher<int>>();
        using var cancellation = new CancellationTokenSource();

        var task = batcher.ExecuteAsync(
            1,
            static (IReadOnlyList<int> _, CancellationToken _) => BlockingProbe.Current.HandleAsync(),
            cancellation.Token);
        await probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        probe.Release.TrySetResult();
        await task;
    }

    [Fact]
    public async Task ExecuteAsync_FullModeFail_ReleasesCapacityAfterHandlerFailure()
    {
        var probe = new FailureThenSuccessProbe();
        FailureThenSuccessProbe.Current = probe;
        var services = new ServiceCollection();
        services.AddDelegateRequestBatcher<int>(options =>
        {
            options.BatchSize = 1;
            options.MaxPendingRequests = 1;
            options.FullMode = RequestBatchFullMode.Fail;
        });
        await using var provider = services.BuildServiceProvider();
        var batcher = provider.GetRequiredService<IRequestBatcher<int>>();

        for (var request = 1; request <= 2; request++)
        {
            var task = batcher.ExecuteAsync(
                request,
                static (IReadOnlyList<int> _) => FailureThenSuccessProbe.Current.Invoke());

            if (request == 1)
            {
                await Assert.ThrowsAsync<DelegateFailureException>(() => task);
            }
            else
            {
                await task;
            }
        }
    }

    [Fact]
    public async Task ExecuteAsync_NamedPipelines_DrainTogether()
    {
        var firstProbe = new DrainProbe();
        var secondProbe = new DrainProbe();
        DrainProbe.First = firstProbe;
        DrainProbe.Second = secondProbe;
        var services = new ServiceCollection();
        services.AddDelegateRequestBatcher<int>("first");
        services.AddDelegateRequestBatcher<int>("second");
        await using var provider = services.BuildServiceProvider();
        var first = provider.GetRequiredKeyedService<IRequestBatcher<int>>("first");
        var second = provider.GetRequiredKeyedService<IRequestBatcher<int>>("second");

        var firstTask = first.ExecuteAsync(1, static (IReadOnlyList<int> _) => DrainProbe.First.HandleAsync());
        var secondTask = second.ExecuteAsync(2, static (IReadOnlyList<int> _) => DrainProbe.Second.HandleAsync());

        await Task.WhenAll(firstProbe.Started.Task, secondProbe.Started.Task);
        var disposeTask = provider.DisposeAsync().AsTask();
        Assert.False(disposeTask.IsCompleted);
        firstProbe.Release.TrySetResult();
        secondProbe.Release.TrySetResult();
        await Task.WhenAll(firstTask, secondTask, disposeTask);
    }

    [Fact]
    public async Task ExecuteAsync_TaskReturningHandler_CompletesSuccessfully()
    {
        var services = new ServiceCollection();
        services.AddDelegateRequestBatcher<int>();
        await using var provider = services.BuildServiceProvider();
        var batcher = provider.GetRequiredService<IRequestBatcher<int>>();

        await batcher.ExecuteAsync(
            1,
            static (IReadOnlyList<int> _) => Task.CompletedTask);
    }

    [Fact]
    public async Task ExecuteAsync_SameLambdaCallSite_ReusesBinding()
    {
        var services = new ServiceCollection();
        services.AddDelegateRequestBatcher<int>();
        await using var provider = services.BuildServiceProvider();
        var batcher = provider.GetRequiredService<IRequestBatcher<int>>();

        for (var request = 1; request <= 2; request++)
        {
            await batcher.ExecuteAsync(
                request,
                static (IReadOnlyList<int> _) => { });
        }
    }

    [Fact]
    public async Task ExecuteAsync_InvalidDelegateSignatures_RejectWithoutBinding()
    {
        var services = new ServiceCollection();
        services.AddDelegateRequestBatcher<int>();
        await using var provider = services.BuildServiceProvider();
        var batcher = provider.GetRequiredService<IRequestBatcher<int>>();

        Action<IReadOnlyList<int>> asyncVoid = async static _ => await Task.Yield();
        Func<IReadOnlyList<int>, Task<int>> taskWithResult =
            static _ => Task.FromResult(1);
        Action<int> wrongFirstParameter = static _ => { };
        Func<IReadOnlyList<int>, CancellationToken, CancellationToken, Task> duplicateTokens =
            static (_, _, _) => Task.CompletedTask;
        Action<IReadOnlyList<int>> multicast = static _ => { };
        multicast += static _ => { };

        foreach (var invalid in new Delegate[]
        {
            asyncVoid,
            taskWithResult,
            wrongFirstParameter,
            duplicateTokens,
            multicast
        })
        {
            await Assert.ThrowsAsync<ArgumentException>(
                () => batcher.ExecuteAsync(1, invalid));
        }

        await batcher.ExecuteAsync(2, static (IReadOnlyList<int> _) => { });
    }

    [Fact]
    public async Task ExecuteAsync_ConcurrentDifferentHandlers_BindsOneAndRejectsTheOther()
    {
        var processed = new ConcurrentQueue<int>();
        DifferentHandlerProbe.Current = processed;
        var services = new ServiceCollection();
        services.AddDelegateRequestBatcher<int>();
        await using var provider = services.BuildServiceProvider();
        var batcher = provider.GetRequiredService<IRequestBatcher<int>>();
        var firstReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = CaptureSubmissionAsync(
            batcher,
            1,
            (Delegate)(Action<IReadOnlyList<int>>)(static requests => DifferentHandlerProbe.Record(requests)),
            firstReady,
            secondReady,
            release);
        var second = CaptureSubmissionAsync(
            batcher,
            2,
            (Delegate)(Action<IReadOnlyList<int>>)(static requests => DifferentHandlerProbe.Record(requests)),
            secondReady,
            firstReady,
            release);

        await Task.WhenAll(firstReady.Task, secondReady.Task);
        release.TrySetResult();
        var outcomes = await Task.WhenAll(first, second);

        Assert.Equal(1, outcomes.Count(static exception => exception is null));
        Assert.Equal(1, outcomes.Count(static exception => exception is InvalidOperationException));
        Assert.Single(processed);
        Assert.Contains(processed.Single(), new[] { 1, 2 });
    }

    [Fact]
    public async Task ExecuteAsync_NullRequestList_DoesNotBindHandler()
    {
        var services = new ServiceCollection();
        services.AddDelegateRequestBatcher<int>();
        await using var provider = services.BuildServiceProvider();
        var batcher = provider.GetRequiredService<IRequestBatcher<int>>();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => batcher.ExecuteAsync(
                (IEnumerable<int>)null!,
                static (IReadOnlyList<int> _) => { }));

        await batcher.ExecuteAsync(1, static (IReadOnlyList<int> _) => { });
    }

    [Fact]
    public async Task ExecuteAsync_MissingResponse_FailsTheBatch()
    {
        var services = new ServiceCollection();
        services.AddDelegateRequestBatcher<int, string?>();
        await using var provider = services.BuildServiceProvider();
        var batcher = provider.GetRequiredService<IRequestBatcher<int, string?>>();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => batcher.ExecuteAsync(
                1,
                static (IReadOnlyList<RequestBatchEntry<int, string?>> _) => { }));

        Assert.Contains("without assigning a response", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_AsyncScopeDisposal_AwaitsBeforeCompletion()
    {
        var disposal = new AsyncDisposalProbe();
        var services = new ServiceCollection();
        services.AddSingleton(disposal);
        services.AddScoped<AsyncDisposableDependency>();
        services.AddDelegateRequestBatcher<int>();
        await using var provider = services.BuildServiceProvider();
        var batcher = provider.GetRequiredService<IRequestBatcher<int>>();

        var task = batcher.ExecuteAsync(
            1,
            static (IReadOnlyList<int> _, AsyncDisposableDependency dependency) =>
            {
                dependency.Probe.InvocationCount++;
            });

        try
        {
            await disposal.DisposeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(task.IsCompleted);
        }
        finally
        {
            disposal.Release.TrySetResult();
        }
        await task;
        Assert.Equal(1, disposal.DisposedCount);
    }

    [Fact]
    public async Task ExecuteAsync_AsyncScopeDisposalFailure_ReleasesCapacity()
    {
        var disposal = new AsyncDisposalProbe { ThrowOnDispose = true };
        var services = new ServiceCollection();
        services.AddSingleton(disposal);
        services.AddScoped<AsyncDisposableDependency>();
        services.AddDelegateRequestBatcher<int>(options =>
        {
            options.BatchSize = 1;
            options.MaxPendingRequests = 1;
        });
        await using var provider = services.BuildServiceProvider();
        var batcher = provider.GetRequiredService<IRequestBatcher<int>>();

        for (var request = 1; request <= 2; request++)
        {
            var task = batcher.ExecuteAsync(
                request,
                static (IReadOnlyList<int> _, AsyncDisposableDependency dependency) => { });
            if (request == 1)
            {
                await disposal.DisposeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                disposal.Release.TrySetResult();
                await Assert.ThrowsAsync<AsyncDisposalException>(() => task);
                disposal.ThrowOnDispose = false;
            }
            else
            {
                await task;
            }
        }
    }

    [Fact]
    public async Task ExecuteAsync_FailingDependencyResolution_AllowsSameHandlerLater()
    {
        var dependency = new FlakyDependency();
        var services = new ServiceCollection();
        services.AddSingleton(dependency);
        services.AddScoped<FlakyScopedDependency>(_ => dependency.Create());
        services.AddDelegateRequestBatcher<int>();
        await using var provider = services.BuildServiceProvider();
        var batcher = provider.GetRequiredService<IRequestBatcher<int>>();

        for (var request = 1; request <= 2; request++)
        {
            var task = batcher.ExecuteAsync(
                request,
                static (IReadOnlyList<int> _, FlakyScopedDependency _) => { });
            if (request == 1)
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() => task);
            }
            else
            {
                await task;
            }
        }

        Assert.Equal(2, dependency.ResolutionAttempts);
    }

    private static async Task<Exception?> CaptureSubmissionAsync(
        IRequestBatcher<int> batcher,
        int request,
        Delegate handler,
        TaskCompletionSource ready,
        TaskCompletionSource otherReady,
        TaskCompletionSource release)
    {
        ready.TrySetResult();
        await Task.WhenAll(ready.Task, otherReady.Task, release.Task);
        try
        {
            await batcher.ExecuteAsync(request, handler);
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private sealed class NoOpTypedHandler : IRequestBatchHandler<int>
    {
        public ValueTask HandleAsync(
            IReadOnlyList<int> requests,
            CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
    }

    private sealed class ThirdPartyBatcher : IRequestBatcher<int>
    {
        public Task ExecuteAsync(int request, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task ExecuteAsync(
            IEnumerable<int> requests,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class RoutingProbe
    {
        public ConcurrentQueue<int[]> Batches { get; } = new();
    }

    private static class DifferentHandlerProbe
    {
        public static ConcurrentQueue<int> Current { get; set; } = new();

        public static void Record(IReadOnlyList<int> requests)
        {
            foreach (var request in requests)
            {
                Current.Enqueue(request);
            }
        }
    }

    private sealed class DrainProbe
    {
        public static DrainProbe First { get; set; } = new();
        public static DrainProbe Second { get; set; } = new();

        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask HandleAsync()
        {
            Started.TrySetResult();
            await Release.Task.ConfigureAwait(false);
        }
    }

    private sealed class Multiplier(int value)
    {
        public int Value { get; } = value;
    }

    private sealed class ScopeState
    {
        public int Invocations;
        public int Disposals;
    }

    private sealed class ScopedDependency(ScopeState state) : IAsyncDisposable
    {
        public ScopeState State { get; } = state;

        public ValueTask DisposeAsync()
        {
            State.Disposals++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class DelegateFailureException : Exception;

    private static class DelegateFailureState
    {
        public static DelegateFailureException Exception { get; set; } = new();
    }

    private sealed class BlockingProbe
    {
        public static BlockingProbe Current { get; set; } = new();

        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask HandleAsync()
        {
            Started.TrySetResult();
            await Release.Task.ConfigureAwait(false);
        }
    }

    private sealed class FailureThenSuccessProbe
    {
        public static FailureThenSuccessProbe Current { get; set; } = new();
        private int _calls;

        public DelegateFailureException Exception { get; } = new();

        public ValueTask Invoke()
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                return ValueTask.FromException(Exception);
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class AsyncDisposalProbe
    {
        public TaskCompletionSource DisposeStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int InvocationCount;
        public int DisposedCount;
        public bool ThrowOnDispose;
    }

    private sealed class AsyncDisposableDependency(AsyncDisposalProbe probe) : IAsyncDisposable
    {
        public AsyncDisposalProbe Probe { get; } = probe;

        public async ValueTask DisposeAsync()
        {
            Probe.DisposeStarted.TrySetResult();
            await Probe.Release.Task.ConfigureAwait(false);
            if (Probe.ThrowOnDispose)
            {
                throw new AsyncDisposalException();
            }

            Probe.DisposedCount++;
        }
    }

    private sealed class AsyncDisposalException : Exception;

    private sealed class FlakyDependency
    {
        public int ResolutionAttempts;

        public FlakyScopedDependency Create()
        {
            if (Interlocked.Increment(ref ResolutionAttempts) == 1)
            {
                throw new InvalidOperationException("The scoped dependency was unavailable.");
            }

            return new FlakyScopedDependency();
        }
    }

    private sealed class FlakyScopedDependency;
}
