using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using RequestBatcher.Diagnostics;
using RequestBatcher.PendingRequests;

namespace RequestBatcher.Tests.Diagnostics;

[Collection("Tracing")]
public sealed class RequestBatcherTracingTests
{
    [Fact]
    public async Task ExecuteAsync_TwoCallersShareBatch_LinksCallsAndMeasuresWholeWait()
    {
        using var capture = new TraceCapture();
        var started = NewSource();
        var release = NewSource();
        ActivitySpanId downstreamParent = default;
        await using var provider = CreateProvider(async (requests, _) =>
        {
            if (requests.Contains(0))
            {
                started.TrySetResult();
                await release.Task;
            }
            else
            {
                using var downstream = new Activity("database").Start();
                downstreamParent = downstream.ParentSpanId;
            }
        });
        var batcher = provider.GetRequiredService<IRequestBatcher<int>>();
        var first = batcher.ExecuteAsync(0);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task a;
        Task b;
        ActivityContext parentA;
        ActivityContext parentB;
        try
        {
            using (var parent = new Activity("HTTP A").Start())
            {
                parentA = parent.Context;
                a = batcher.ExecuteAsync(1);
                Assert.Same(parent, Activity.Current);
            }

            using (var parent = new Activity("HTTP B").Start())
            {
                parentB = parent.Context;
                b = batcher.ExecuteAsync(2);
                Assert.Same(parent, Activity.Current);
            }

            Assert.False(a.IsCompleted);
            Assert.False(b.IsCompleted);
            Assert.DoesNotContain(capture.Stopped, span => span.OperationName == RequestBatcherTracing.CallName);
        }
        finally
        {
            release.TrySetResult();
        }

        await Task.WhenAll(first, a, b).WaitAsync(TimeSpan.FromSeconds(5));
        var batch = Assert.Single(capture.Stopped, span =>
            span.OperationName == RequestBatcherTracing.BatchName &&
            Equals(span.GetTagItem("requestbatcher.batch.size"), 2));
        var callA = Assert.Single(capture.Calls, span => span.ParentSpanId == parentA.SpanId);
        var callB = Assert.Single(capture.Calls, span => span.ParentSpanId == parentB.SpanId);
        Assert.Equal(parentA.TraceId, callA.TraceId);
        Assert.Equal(parentB.TraceId, callB.TraceId);
        Assert.Equal(ActivityKind.Internal, callA.Kind);
        Assert.Equal(ActivityKind.Consumer, batch.Kind);
        Assert.Equal(default, batch.ParentSpanId);
        Assert.NotEqual(callA.TraceId, batch.TraceId);
        Assert.Equal(batch.SpanId, downstreamParent);
        Assert.Equal(2, batch.Links.Count());
        Assert.Contains(batch.Links, link => link.Context == callA.Context);
        Assert.Contains(batch.Links, link => link.Context == callB.Context);
        Assert.Equal(batch.Context, Assert.Single(callA.Links).Context);
        Assert.Equal(batch.Context, Assert.Single(callB.Links).Context);
        Assert.True(callA.StartTimeUtc <= batch.StartTimeUtc);
        Assert.True(callA.StartTimeUtc + callA.Duration >= batch.StartTimeUtc + batch.Duration);
        Assert.True(callB.StartTimeUtc + callB.Duration >= batch.StartTimeUtc + batch.Duration);
    }

    [Fact]
    public async Task ExecuteAsync_ExplicitSubmissionSpansBatches_LinksOneCallFromEveryBatch()
    {
        using var capture = new TraceCapture();
        await using var provider = CreateProvider((_, _) => ValueTask.CompletedTask, batchSize: 1);
        var batcher = provider.GetRequiredService<IRequestBatcher<int>>();
        await batcher.ExecuteAsync(new[] { 1, 2, 3 }).WaitAsync(TimeSpan.FromSeconds(5));

        var call = Assert.Single(capture.Calls);
        Assert.Equal(3, capture.Batches.Count());
        Assert.All(capture.Batches, batch => Assert.Equal(call.Context, Assert.Single(batch.Links).Context));
        Assert.Equal(3, call.Links.Count());
        Assert.All(capture.Batches, batch => Assert.Contains(call.Links, link => link.Context == batch.Context));
    }

    [Fact]
    public async Task ExecuteAsync_CanceledQueuedCall_IsOmittedFromBatchLinks()
    {
        using var capture = new TraceCapture();
        var started = NewSource();
        var release = NewSource();
        await using var provider = CreateProvider(async (requests, _) =>
        {
            if (requests.Contains(0))
            {
                started.TrySetResult();
                await release.Task;
            }
        });
        var batcher = provider.GetRequiredService<IRequestBatcher<int>>();
        var first = batcher.ExecuteAsync(0);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var cancellation = new CancellationTokenSource();
        var canceled = batcher.ExecuteAsync(1, cancellation.Token);
        var active = batcher.ExecuteAsync(new[] { 2, 3 });
        try
        {
            cancellation.Cancel();
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);
            Assert.Equal(cancellation.Token, error.CancellationToken);
        }
        finally
        {
            release.TrySetResult();
        }

        await Task.WhenAll(first, active).WaitAsync(TimeSpan.FromSeconds(5));
        var canceledCall = Assert.Single(capture.Calls, span => Equals(span.GetTagItem("requestbatcher.canceled"), true));
        Assert.Equal(ActivityStatusCode.Unset, canceledCall.Status);
        Assert.Empty(canceledCall.Links);
        Assert.All(capture.Batches, batch => Assert.DoesNotContain(batch.Links, link => link.Context == canceledCall.Context));
        var activeBatch = Assert.Single(capture.Batches, span => Equals(span.GetTagItem("requestbatcher.batch.size"), 2));
        Assert.Single(activeBatch.Links);
        var activeCall = Assert.Single(capture.Calls, call => call.Context == activeBatch.Links.Single().Context);
        Assert.Equal(activeBatch.Context, Assert.Single(activeCall.Links).Context);
    }

    [Fact]
    public async Task ExecuteAsync_CancellationAfterDispatch_RecordsActualSuccess()
    {
        using var capture = new TraceCapture();
        var started = NewSource();
        var release = NewSource();
        await using var provider = CreateProvider(async (_, _) =>
        {
            started.SetResult();
            await release.Task;
        });
        using var cancellation = new CancellationTokenSource();
        var task = provider.GetRequiredService<IRequestBatcher<int>>().ExecuteAsync(1, cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        release.SetResult();
        await task.WaitAsync(TimeSpan.FromSeconds(5));
        var call = Assert.Single(capture.Calls);
        Assert.Null(call.GetTagItem("requestbatcher.canceled"));
        Assert.Equal(ActivityStatusCode.Unset, call.Status);
    }

    [Fact]
    public async Task ExecuteAsync_HandlerThrowsCancellationException_PreservesFaultedTaskAndOriginalError()
    {
        using var capture = new TraceCapture();
        var expected = new OperationCanceledException("handler failure");
        await using var provider = CreateProvider((_, _) => ValueTask.FromException(expected));
        var task = provider.GetRequiredService<IRequestBatcher<int>>().ExecuteAsync(1);
        Assert.Same(expected, await Assert.ThrowsAsync<OperationCanceledException>(() => task));
        Assert.True(task.IsFaulted);
        Assert.All(capture.Stopped, span => Assert.Equal(ActivityStatusCode.Error, span.Status));
        Assert.Equal(typeof(OperationCanceledException).FullName, Assert.Single(capture.Calls).GetTagItem("error.type"));
    }

    [Fact]
    public async Task ExecuteAsync_EmptyPreCanceledAndStoppedCalls_EndWithoutProcessingSpans()
    {
        using var capture = new TraceCapture();
        await using var provider = CreateProvider((_, _) => ValueTask.CompletedTask);
        var batcher = provider.GetRequiredService<RequestBatchCoordinator<int>>();
        await batcher.ExecuteAsync(Array.Empty<int>());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => batcher.ExecuteAsync(1, cancellation.Token));
        await batcher.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => batcher.ExecuteAsync(2));
        Assert.Equal(3, capture.Calls.Count());
        Assert.Empty(capture.Batches);
    }

    [Fact]
    public async Task TraceCall_MultipleFaults_PreservesAllOriginalExceptions()
    {
        using var capture = new TraceCapture();
        var first = new InvalidOperationException();
        var second = new ArgumentException();
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = RequestBatcherTracing.TraceCall("test", pending.Task, static (state, _) => state);
        pending.SetException(new Exception[] { first, second });
        Assert.Same(first, await Assert.ThrowsAsync<InvalidOperationException>(() => task));
        Assert.Equal(new Exception[] { first, second }, task.Exception!.InnerExceptions);
    }

    [Fact]
    public async Task ExecuteAsync_ListenerDropsCall_StillLinksAvailableCallerContext()
    {
        using var capture = new TraceCapture(options => options.Name == RequestBatcherTracing.CallName
            ? ActivitySamplingResult.None
            : ActivitySamplingResult.AllDataAndRecorded);
        await using var provider = CreateProvider((_, _) => ValueTask.CompletedTask);
        using var parent = new Activity("HTTP").Start();
        await provider.GetRequiredService<IRequestBatcher<int>>().ExecuteAsync(1);
        Assert.Same(parent, Activity.Current);
        Assert.Empty(capture.Calls);
        Assert.Equal(parent.Context, Assert.Single(Assert.Single(capture.Batches).Links).Context);
        Assert.Empty(parent.Links);
    }

    [Fact]
    public async Task ExecuteCoreAsync_NoListener_ReturnsOriginalCompletionTask()
    {
        Assert.False(RequestBatcherTracing.Source.HasListeners());
        await using var provider = CreateProvider((_, _) => ValueTask.CompletedTask);
        var completion = new RequestCompletion();
        var task = provider.GetRequiredService<RequestBatchCoordinator<int>>().ExecuteCoreAsync(1, completion, default);
        Assert.Same(completion.Task, task);
        await task;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_NamedDelegateResponse_HasOneCallSpanIncludingResult(bool batchSubmission)
    {
        // Repeat with a fresh provider to prove cached tracing callbacks use the current batcher.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var capture = new TraceCapture();
            var services = new ServiceCollection();
            services.AddDelegateRequestBatcher<int, string>("lookup");
            await using var provider = services.BuildServiceProvider();
            var batcher = provider.GetRequiredKeyedService<IRequestBatcher<int, string>>("lookup");
            using var parent = new Activity("HTTP").Start();
            if (batchSubmission)
            {
                var result = await batcher.ExecuteAsync(new[] { 1, 2 },
                    static (IReadOnlyList<RequestBatchEntry<int, string>> entries) =>
                        entries.SetResponses(static value => value.ToString()));
                Assert.Equal(new[] { "1", "2" }, result);
            }
            else
            {
                var result = await batcher.ExecuteAsync(1,
                    static (IReadOnlyList<RequestBatchEntry<int, string>> entries) =>
                        entries.SetResponses(static value => value.ToString()));
                Assert.Equal("1", result);
            }

            Assert.Same(parent, Activity.Current);
            var call = Assert.Single(capture.Calls);
            Assert.Equal(parent.SpanId, call.ParentSpanId);
            Assert.All(capture.Batches, batch => Assert.Equal(call.Context, Assert.Single(batch.Links).Context));
            Assert.Equal(capture.Batches.Count(), call.Links.Count());
            Assert.All(capture.Batches, batch => Assert.Contains(call.Links, link => link.Context == batch.Context));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_DelegateWithoutResponse_HasOneCallSpan(bool batchSubmission)
    {
        // Repeat with a fresh provider to prove cached tracing callbacks use the current batcher.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var capture = new TraceCapture();
            var services = new ServiceCollection();
            services.AddDelegateRequestBatcher<int>();
            await using var provider = services.BuildServiceProvider();
            var batcher = provider.GetRequiredService<IRequestBatcher<int>>();
            if (batchSubmission)
            {
                await batcher.ExecuteAsync(new[] { 1, 2 }, static (IReadOnlyList<int> _) => { });
            }
            else
            {
                await batcher.ExecuteAsync(1, static (IReadOnlyList<int> _) => { });
            }

            Assert.Single(capture.Calls);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_ScopedResponseHandlerDisposal_WaitsToEndCallAndBatch(bool batchSubmission)
    {
        // Repeat with a fresh provider to prove cached tracing callbacks use the current batcher.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var capture = new TraceCapture();
            var probe = new DisposalProbe();
            var services = new ServiceCollection();
            services.AddSingleton(probe);
            services.AddRequestBatcher<int, string, ScopedResponseHandler>(ServiceLifetime.Scoped);
            await using var provider = services.BuildServiceProvider();
            var batcher = provider.GetRequiredService<IRequestBatcher<int, string>>();
            Task task = batchSubmission ? batcher.ExecuteAsync(new[] { 1, 2 }) : batcher.ExecuteAsync(1);
            await probe.Disposing.Task.WaitAsync(TimeSpan.FromSeconds(5));
            try
            {
                Assert.False(task.IsCompleted);
                Assert.Empty(capture.Stopped);
            }
            finally
            {
                probe.Release.TrySetResult();
            }

            await task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Single(capture.Calls);
            var batch = Assert.Single(capture.Batches);
            Assert.Equal(batch.Context, probe.Context);
        }
    }

    [Fact]
    public async Task ExecuteCoreAsync_NoSubmissionContext_CreatesUnlinkedBatchRoot()
    {
        using var capture = new TraceCapture();
        await using var provider = CreateProvider((_, _) => ValueTask.CompletedTask);
        await provider.GetRequiredService<RequestBatchCoordinator<int>>().ExecuteCoreAsync(1, null, default);
        var batch = Assert.Single(capture.Batches);
        Assert.Equal(default, batch.ParentSpanId);
        Assert.Empty(batch.Links);
    }

    [Theory]
    [InlineData(RequestBatchFullMode.Wait)]
    [InlineData(RequestBatchFullMode.Fail)]
    public async Task ExecuteAsync_QueueAtCapacity_TracesWaitOrRejection(RequestBatchFullMode fullMode)
    {
        using var capture = new TraceCapture();
        var started = NewSource();
        var release = NewSource();
        var services = new ServiceCollection();
        services.AddSingleton<RequestBatchHandler<int>>(async (requests, _) =>
        {
            if (requests.Contains(0))
            {
                started.TrySetResult();
                await release.Task;
            }
        });
        services.AddRequestBatcher<int, TestHandler>(ServiceLifetime.Singleton, options =>
        {
            options.BatchSize = 1;
            options.MaxConcurrency = 1;
            options.MaxPendingRequests = 1;
            options.FullMode = fullMode;
        });
        await using var provider = services.BuildServiceProvider();
        var batcher = provider.GetRequiredService<IRequestBatcher<int>>();
        var first = batcher.ExecuteAsync(0);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var queued = batcher.ExecuteAsync(1);
        using var parent = new Activity("capacity-waiter").Start();
        var last = batcher.ExecuteAsync(2);
        try
        {
            if (fullMode == RequestBatchFullMode.Wait)
            {
                Assert.False(last.IsCompleted);
                Assert.DoesNotContain(capture.Calls, span => span.ParentSpanId == parent.SpanId);
            }
            else
            {
                await Assert.ThrowsAsync<RequestBatchQueueFullException>(() => last);
                var call = Assert.Single(capture.Calls);
                Assert.Equal(ActivityStatusCode.Error, call.Status);
            }
        }
        finally
        {
            release.TrySetResult();
        }

        await Task.WhenAll(first, queued).WaitAsync(TimeSpan.FromSeconds(5));
        if (fullMode == RequestBatchFullMode.Wait)
        {
            await last.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.Same(parent, Activity.Current);
        Assert.Equal(3, capture.Calls.Count());
        Assert.Equal(fullMode == RequestBatchFullMode.Wait ? 3 : 2, capture.Batches.Count());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_ConcurrentBatches_CollectsEveryReverseLinkBeforeCallStops(bool fail)
    {
        using var capture = new TraceCapture();
        var allStarted = NewSource();
        var release = NewSource();
        var started = 0;
        const int batchCount = 8;
        var expected = new InvalidOperationException("batch failed");
        await using var provider = CreateProvider(async (_, _) =>
        {
            if (Interlocked.Increment(ref started) == batchCount)
            {
                allStarted.TrySetResult();
            }

            await release.Task;
            if (fail)
            {
                throw expected;
            }
        }, batchSize: 1, maxConcurrency: batchCount);
        var task = provider.GetRequiredService<IRequestBatcher<int>>()
            .ExecuteAsync(Enumerable.Range(0, batchCount));
        try
        {
            await allStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Empty(capture.Calls);
        }
        finally
        {
            release.TrySetResult();
        }

        if (fail)
        {
            await Assert.ThrowsAnyAsync<Exception>(() => task.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(task.IsFaulted);
        }
        else
        {
            await task.WaitAsync(TimeSpan.FromSeconds(5));
        }

        var call = Assert.Single(capture.Calls);
        Assert.Equal(batchCount, capture.Batches.Count());
        Assert.Equal(batchCount, call.Links.Count());
        Assert.All(capture.Batches, batch => Assert.Contains(call.Links, link => link.Context == batch.Context));
    }

    [Fact]
    public async Task ExecuteAsync_DroppedBatch_DoesNotAddReverseLink()
    {
        using var capture = new TraceCapture(options => options.Name == RequestBatcherTracing.BatchName
            ? ActivitySamplingResult.None
            : ActivitySamplingResult.AllDataAndRecorded);
        await using var provider = CreateProvider((_, _) => ValueTask.CompletedTask);
        await provider.GetRequiredService<IRequestBatcher<int>>().ExecuteAsync(1);
        Assert.Empty(capture.Batches);
        Assert.Empty(Assert.Single(capture.Calls).Links);
    }

    private sealed class DisposalProbe
    {
        public TaskCompletionSource Disposing { get; } = NewSource();
        public TaskCompletionSource Release { get; } = NewSource();
        public ActivityContext Context { get; set; }
    }

    private sealed class ScopedResponseHandler(DisposalProbe probe) : IRequestBatchHandler<int, string>, IAsyncDisposable
    {
        public ValueTask HandleAsync(
            IReadOnlyList<RequestBatchEntry<int, string>> requests,
            CancellationToken cancellationToken = default)
        {
            requests.SetResponses(static request => request.ToString());
            return ValueTask.CompletedTask;
        }

        public async ValueTask DisposeAsync()
        {
            probe.Context = Activity.Current!.Context;
            probe.Disposing.SetResult();
            await probe.Release.Task;
            Assert.Equal(probe.Context, Activity.Current!.Context);
        }
    }

    private static TaskCompletionSource NewSource() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static ServiceProvider CreateProvider(
        RequestBatchHandler<int> handler, int batchSize = 16, int maxConcurrency = 1)
    {
        var services = new ServiceCollection();
        services.AddSingleton(handler);
        services.AddRequestBatcher<int, TestHandler>(ServiceLifetime.Singleton, options =>
        {
            options.BatchSize = batchSize;
            options.MaxConcurrency = maxConcurrency;
        });
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private sealed class TestHandler(RequestBatchHandler<int> handler) : IRequestBatchHandler<int>
    {
        public ValueTask HandleAsync(IReadOnlyList<int> requests, CancellationToken cancellationToken = default) =>
            handler(requests, cancellationToken);
    }

    private sealed class TraceCapture : IDisposable
    {
        private readonly ActivityListener _listener;
        public ConcurrentQueue<Activity> Stopped { get; } = new();
        public IEnumerable<Activity> Calls => Stopped.Where(span => span.OperationName.StartsWith(
            RequestBatcherTracing.CallName, StringComparison.Ordinal));
        public IEnumerable<Activity> Batches => Stopped.Where(span => span.OperationName.StartsWith(
            RequestBatcherTracing.BatchName, StringComparison.Ordinal));

        public TraceCapture(Func<ActivityCreationOptions<ActivityContext>, ActivitySamplingResult>? sample = null)
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == RequestBatcherTracing.SourceName,
                Sample = (ref ActivityCreationOptions<ActivityContext> options) =>
                    sample?.Invoke(options) ?? ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = Stopped.Enqueue,
            };
            ActivitySource.AddActivityListener(_listener);
        }

        public void Dispose() => _listener.Dispose();
    }
}
