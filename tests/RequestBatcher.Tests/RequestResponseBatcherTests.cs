using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;

namespace RequestBatcher.Tests;

public sealed class RequestResponseBatcherTests
{
    [Fact]
    public async Task ProcessAsync_HandlerSetsResponse_ReturnsResponse()
    {
        var handler = new Mock<IRequestBatchHandler<ResponseRequest, string>>(MockBehavior.Strict);
        handler
            .Setup(subject => subject.HandleAsync(
                It.Is<IReadOnlyList<RequestBatchItem<ResponseRequest, string>>>(
                    items => items.Count == 1 && items[0].Request.Value == 42),
                It.IsAny<CancellationToken>()))
            .Returns((IReadOnlyList<RequestBatchItem<ResponseRequest, string>> items, CancellationToken _) =>
            {
                items[0].SetResponse("answer");
                return ValueTask.CompletedTask;
            })
            .Verifiable();

        var services = new ServiceCollection();
        services.AddRequestBatcher<ResponseRequest, string>(
            handler.Object.HandleAsync,
            ServiceLifetime.Singleton);
        await using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        var batcher = provider.GetRequiredService<IRequestBatcher<ResponseRequest, string>>();

        var actual = await batcher.ProcessAsync(new ResponseRequest(42)).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("answer", actual);
        handler.Verify();
        handler.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ProcessAsync_ExplicitBatch_ReturnsResponsesInInputOrder()
    {
        await using var provider = CreateProvider<int, string>(
            (items, _) =>
            {
                items.SetResponses(request => $"response-{request}");
                return ValueTask.CompletedTask;
            },
            options =>
            {
                options.BatchSize = 1;
                options.MaxConcurrency = 2;
                options.UsePartitionKey(static request => request % 2);
            });
        var batcher = provider.GetRequiredService<IRequestBatcher<int, string>>();

        var actual = await batcher
            .ProcessAsync([3, 1, 2], CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(new[] { "response-3", "response-1", "response-2" }, actual);
    }

    [Fact]
    public async Task ProcessAsync_ResponseHandlerFails_PropagatesOriginalException()
    {
        var expected = new TestBatchException();

        await using var provider = CreateProvider<int, string>(
            (_, _) => ValueTask.FromException(expected),
            options => options.BatchSize = 2);
        var batcher = provider.GetRequiredService<IRequestBatcher<int, string>>();

        var actual = await Assert.ThrowsAsync<TestBatchException>(() => batcher.ProcessAsync([1, 2]));

        Assert.Same(expected, actual);
    }

    [Fact]
    public async Task ProcessAsync_HandlerLeavesResponseUnset_FailsHandlerBatch()
    {
        await using var provider = CreateProvider<int, string>(
            (items, _) =>
            {
                items[0].SetResponse("only-first");
                return ValueTask.CompletedTask;
            },
            options => options.BatchSize = 2);
        var batcher = provider.GetRequiredService<IRequestBatcher<int, string>>();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => batcher.ProcessAsync([1, 2]));

        Assert.Contains("without assigning a response", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProcessAsync_HandlerSetsResponseTwice_FailsHandlerBatch()
    {
        await using var provider = CreateProvider<int, string>(
            (items, _) =>
            {
                items[0].SetResponse("first");
                items[0].SetResponse("second");
                return ValueTask.CompletedTask;
            },
            options => options.BatchSize = 2);
        var batcher = provider.GetRequiredService<IRequestBatcher<int, string>>();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => batcher.ProcessAsync([1, 2]));

        Assert.Contains("already been assigned", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SetResponse_SameItemTwice_PreservesFirstResponse()
    {
        var item = new RequestBatchItem<int, string>(1);

        item.SetResponse("first");

        var exception = Assert.Throws<InvalidOperationException>(() => item.SetResponse("second"));

        Assert.Contains("already been assigned", exception.Message, StringComparison.Ordinal);
        Assert.Equal("first", item.GetResponse());
    }

    [Fact]
    public void SetResponses_EnumerableMatchesInputOrder_AssignsResponses()
    {
        var items = CreateItems();
        var enumerationCount = 0;

        IEnumerable<string> Responses()
        {
            enumerationCount++;
            yield return "first";
            yield return "second";
        }

        items.SetResponses(Responses());

        Assert.Equal(1, enumerationCount);
        Assert.Equal("first", items[0].GetResponse());
        Assert.Equal("second", items[1].GetResponse());
    }

    [Fact]
    public void SetResponses_EnumerableHasDifferentCount_DoesNotAssignAnyResponse()
    {
        var items = CreateItems();

        var exception = Assert.Throws<ArgumentException>(() => items.SetResponses(["first"]));

        Assert.Equal("responses", exception.ParamName);
        AssertResponseIsUnset(items[0]);
        AssertResponseIsUnset(items[1]);
    }

    [Fact]
    public void SetResponses_EnumerationFails_DoesNotAssignAnyResponse()
    {
        var items = CreateItems();
        var expected = new TestBatchException();

        IEnumerable<string> Responses()
        {
            yield return "first";
            throw expected;
        }

        var actual = Assert.Throws<TestBatchException>(() => items.SetResponses(Responses()));

        Assert.Same(expected, actual);
        AssertResponseIsUnset(items[0]);
        AssertResponseIsUnset(items[1]);
    }

    [Fact]
    public void SetResponses_ResponseFactoryFails_DoesNotAssignAnyResponse()
    {
        var items = CreateItems();
        var expected = new TestBatchException();

        var actual = Assert.Throws<TestBatchException>(() => items.SetResponses(request =>
            request == 1 ? "first" : throw expected));

        Assert.Same(expected, actual);
        AssertResponseIsUnset(items[0]);
        AssertResponseIsUnset(items[1]);
    }

    [Fact]
    public async Task AddRequestBatcher_ResponsePartitionKeySelectorFails_PropagatesOriginalException()
    {
        var expected = new TestBatchException();
        var invocationCount = 0;

        await using var provider = CreateProvider<int, string>(
            (items, _) =>
            {
                Interlocked.Increment(ref invocationCount);
                items.SetResponses(static request => request.ToString());
                return ValueTask.CompletedTask;
            },
            options => options.UsePartitionKey(request => request >= 0 ? request : throw expected));
        var batcher = provider.GetRequiredService<IRequestBatcher<int, string>>();

        var actual = await Assert.ThrowsAsync<TestBatchException>(() => batcher.ProcessAsync(-1));

        Assert.Same(expected, actual);
        Assert.Equal(0, Volatile.Read(ref invocationCount));
    }

    [Fact]
    public async Task AddRequestBatcher_ResponsePartitionKeySelector_UsesOriginalRequest()
    {
        var selectedKeys = new ConcurrentQueue<int>();

        await using var provider = CreateProvider<KeyedResponseRequest, string>(
            (items, _) =>
            {
                items.SetResponses(static request => request.Value.ToString());
                return ValueTask.CompletedTask;
            },
            options =>
            {
                options.BatchSize = 1;
                options.UsePartitionKey(request =>
                {
                    selectedKeys.Enqueue(request.Key);
                    return request.Key;
                });
            });
        var batcher = provider.GetRequiredService<IRequestBatcher<KeyedResponseRequest, string>>();

        var actual = await batcher.ProcessAsync(
            [new KeyedResponseRequest(2, 20), new KeyedResponseRequest(1, 10), new KeyedResponseRequest(2, 21)]);

        Assert.Equal(new[] { "20", "10", "21" }, actual);
        Assert.Equal(new[] { 1, 2, 2 }, selectedKeys.Order());
    }

    [Fact]
    public void AddRequestBatcher_ResponseAndVoidForSameRequestType_RejectsDuplicatePipeline()
    {
        var services = new ServiceCollection();
        services.AddRequestBatcher<int, string>(
            (items, _) =>
            {
                items.SetResponses(static request => request.ToString());
                return ValueTask.CompletedTask;
            },
            ServiceLifetime.Singleton);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            services.AddRequestBatcher<int>(
                (_, _) => ValueTask.CompletedTask,
                ServiceLifetime.Singleton));

        Assert.Contains("already been registered", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AddRequestBatcher_ScopedResponseHandler_ResolvesOneHandlerPerBatch()
    {
        var probe = new ResponseHandlerLifetimeProbe();
        var services = new ServiceCollection();
        services.AddSingleton(probe);
        services.AddRequestBatcher<int, string, ScopedResponseHandler>(
            ServiceLifetime.Scoped,
            options => options.BatchSize = 1);

        await using (var provider = services.BuildServiceProvider(
                         new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }))
        {
            var batcher = provider.GetRequiredService<IRequestBatcher<int, string>>();

            Assert.Equal("1", await batcher.ProcessAsync(1).WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal("2", await batcher.ProcessAsync(2).WaitAsync(TimeSpan.FromSeconds(5)));
        }

        Assert.Equal(2, probe.HandlerIds.Distinct().Count());
        Assert.Equal(2, probe.DisposeCount);
    }

    [Fact]
    public void AddRequestBatcher_ResponseConfigure_RegistersPublicOptionsAndInvokesCallbackOnce()
    {
        var configureCallCount = 0;
        var services = new ServiceCollection();
        services.AddRequestBatcher<int, string>(
            (items, _) =>
            {
                items.SetResponses(static request => request.ToString());
                return ValueTask.CompletedTask;
            },
            ServiceLifetime.Singleton,
            options =>
            {
                configureCallCount++;
                options.BatchSize = 32;
                options.MaxConcurrency = 4;
                options.MaxPendingRequests = 512;
                options.FullMode = RequestBatchFullMode.Fail;
            });

        using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        var options = provider.GetRequiredService<IOptions<RequestBatchOptions<int>>>().Value;

        Assert.Equal(1, configureCallCount);
        Assert.Equal(32, options.BatchSize);
        Assert.Equal(4, options.MaxConcurrency);
        Assert.Equal(512, options.MaxPendingRequests);
        Assert.Equal(RequestBatchFullMode.Fail, options.FullMode);
    }

    [Fact]
    public async Task ProcessAsync_RequestCanceledBeforeDispatch_SkipsResponseHandler()
    {
        var firstHandlerStarted = NewSource();
        var releaseFirstHandler = NewSource();
        var handledRequests = new ConcurrentQueue<int>();

        await using var provider = CreateProvider<int, string>(
            async (items, _) =>
            {
                var request = Assert.Single(items).Request;
                handledRequests.Enqueue(request);
                if (request == 1)
                {
                    firstHandlerStarted.SetResult();
                    await releaseFirstHandler.Task;
                }

                items[0].SetResponse(request.ToString());
            },
            options =>
            {
                options.BatchSize = 1;
                options.MaxPendingRequests = 1;
            });
        var batcher = provider.GetRequiredService<IRequestBatcher<int, string>>();

        var first = batcher.ProcessAsync(1);
        await firstHandlerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var cancellation = new CancellationTokenSource();
        var canceled = batcher.ProcessAsync(2, cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);
        releaseFirstHandler.SetResult();

        Assert.Equal("1", await first.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(new[] { 1 }, handledRequests);
    }

    [Fact]
    public async Task StopAsync_ResponseBatcherDrainsAcceptedRequestsAndRejectsNewRequests()
    {
        var firstHandlerStarted = NewSource();
        var releaseFirstHandler = NewSource();

        await using var provider = CreateProvider<int, string>(
            async (items, _) =>
            {
                var request = Assert.Single(items).Request;
                if (request == 1)
                {
                    firstHandlerStarted.SetResult();
                    await releaseFirstHandler.Task;
                }

                items[0].SetResponse(request.ToString());
            },
            options => options.BatchSize = 1);
        var batcher = provider.GetRequiredService<IRequestBatcher<int, string>>();
        var coordinator = provider.GetRequiredService<RequestBatchCoordinator<RequestBatchItem<int, string>>>();

        var first = batcher.ProcessAsync(1);
        await firstHandlerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = batcher.ProcessAsync(2);
        var stopping = coordinator.StopAsync().AsTask();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => batcher.ProcessAsync(3));
        releaseFirstHandler.SetResult();

        Assert.Equal(new[] { "1", "2" }, await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5)));
        await stopping.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static ServiceProvider CreateProvider<TRequest, TResponse>(
        RequestBatchHandler<TRequest, TResponse> handler,
        Action<RequestBatchOptions<TRequest>>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddRequestBatcher(handler, ServiceLifetime.Singleton, configure);
        return services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    private static RequestBatchItem<int, string>[] CreateItems() =>
    [
        new RequestBatchItem<int, string>(1),
        new RequestBatchItem<int, string>(2),
    ];

    private static void AssertResponseIsUnset(RequestBatchItem<int, string> item) =>
        Assert.Throws<InvalidOperationException>(() => item.GetResponse());

    private static TaskCompletionSource NewSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public sealed record ResponseRequest(int Value);

    private sealed record KeyedResponseRequest(int Key, int Value);

    private sealed class TestBatchException : Exception;

    private sealed class ResponseHandlerLifetimeProbe
    {
        private int _disposeCount;

        public ConcurrentQueue<Guid> HandlerIds { get; } = new();

        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public void RecordDisposed() => Interlocked.Increment(ref _disposeCount);
    }

    private sealed class ScopedResponseHandler(ResponseHandlerLifetimeProbe probe)
        : IRequestBatchHandler<int, string>, IAsyncDisposable
    {
        private readonly Guid _id = Guid.NewGuid();

        public ValueTask HandleAsync(
            IReadOnlyList<RequestBatchItem<int, string>> requests,
            CancellationToken cancellationToken = default)
        {
            probe.HandlerIds.Enqueue(_id);
            requests.SetResponses(static request => request.ToString());
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            probe.RecordDisposed();
            return ValueTask.CompletedTask;
        }
    }
}
