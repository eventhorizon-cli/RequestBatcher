using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;

namespace RequestBatcher.Tests;

public sealed class RequestResponseBatcherTests
{
    [Fact]
    public async Task ExecuteAsync_HandlerSetsResponse_ReturnsResponse()
    {
        var handler = new Mock<IRequestBatchHandler<ResponseRequest, string>>(MockBehavior.Strict);
        handler
            .Setup(subject => subject.HandleAsync(
                It.Is<IReadOnlyList<RequestBatchEntry<ResponseRequest, string>>>(
                    entries => entries.Count == 1 && entries[0].Request.Value == 42),
                It.IsAny<CancellationToken>()))
            .Returns((IReadOnlyList<RequestBatchEntry<ResponseRequest, string>> entries, CancellationToken _) =>
            {
                entries[0].SetResponse("answer");
                return ValueTask.CompletedTask;
            })
            .Verifiable();

        var services = new ServiceCollection();
        AddResponseHandler<ResponseRequest, string>(services, handler.Object.HandleAsync);
        await using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        var batcher = provider.GetRequiredService<IRequestBatcher<ResponseRequest, string>>();

        var actual = await batcher.ExecuteAsync(new ResponseRequest(42)).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("answer", actual);
        handler.Verify();
        handler.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ExecuteAsync_ExplicitBatch_ReturnsResponsesInInputOrder()
    {
        await using var provider = CreateProvider<int, string>(
            (entries, _) =>
            {
                entries.SetResponses(request => $"response-{request}");
                return ValueTask.CompletedTask;
            },
            options =>
            {
                options.BatchSize = 1;
                options.MaxConcurrency = 2;
                options.UsePartitionKey(static request => request % 2, partitionCount: 2);
            });
        var batcher = provider.GetRequiredService<IRequestBatcher<int, string>>();

        var actual = await batcher
            .ExecuteAsync([3, 1, 2], CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(new[] { "response-3", "response-1", "response-2" }, actual);
    }

    [Fact]
    public async Task ExecuteAsync_StringPartitionKey_PreservesConfiguredPartitionCount()
    {
        await using var provider = CreateProvider<ResponseRequest, string>(
            (entries, _) =>
            {
                entries.SetResponses(static request => request.Value.ToString());
                return ValueTask.CompletedTask;
            },
            options => options.UsePartitionKey(
                request => request.Value.ToString(),
                partitionCount: 2));
        var batcher = provider.GetRequiredService<IRequestBatcher<ResponseRequest, string>>();

        var actual = await batcher.ExecuteAsync(new ResponseRequest(7));

        Assert.Equal("7", actual);
    }

    [Fact]
    public async Task ExecuteAsync_SinglePartition_IgnoresStringSelector()
    {
        var handled = new ConcurrentQueue<int>();
        var handler = new Mock<IRequestBatchHandler<int, string>>(MockBehavior.Strict);
        handler
            .Setup(subject => subject.HandleAsync(
                It.Is<IReadOnlyList<RequestBatchEntry<int, string>>>(entries =>
                    entries.Count == 1 && entries[0].Request >= 1 && entries[0].Request <= 3),
                It.IsAny<CancellationToken>()))
            .Returns((IReadOnlyList<RequestBatchEntry<int, string>> entries, CancellationToken _) =>
            {
                handled.Enqueue(entries[0].Request);
                entries.SetResponses(static request => request.ToString());
                return ValueTask.CompletedTask;
            })
            .Verifiable();

        var services = new ServiceCollection();
        AddResponseHandler<int, string>(services, handler.Object.HandleAsync, configure: options =>
        {
            options.BatchSize = 1;
            options.UsePartitionKey(
                (Func<int, string>)(static (int _) => throw new TestBatchException()),
                partitionCount: 1);
        });
        await using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        var batcher = provider.GetRequiredService<IRequestBatcher<int, string>>();

        Assert.Equal("1", await batcher.ExecuteAsync(1).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(
            new[] { "2", "3" },
            await batcher.ExecuteAsync([2, 3]).WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Equal(new[] { 1, 2, 3 }, handled.Order());
        handler.Verify(subject => subject.HandleAsync(
            It.IsAny<IReadOnlyList<RequestBatchEntry<int, string>>>(),
            It.IsAny<CancellationToken>()), Times.Exactly(3));
        handler.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ExecuteAsync_ResponseHandlerFails_PropagatesOriginalException()
    {
        var expected = new TestBatchException();

        await using var provider = CreateProvider<int, string>(
            (_, _) => ValueTask.FromException(expected),
            options => options.BatchSize = 2);
        var batcher = provider.GetRequiredService<IRequestBatcher<int, string>>();

        var actual = await Assert.ThrowsAsync<TestBatchException>(() => batcher.ExecuteAsync([1, 2]));

        Assert.Same(expected, actual);
    }

    [Fact]
    public async Task ExecuteAsync_HandlerLeavesResponseUnset_FailsHandlerBatch()
    {
        await using var provider = CreateProvider<int, string>(
            (entries, _) =>
            {
                entries[0].SetResponse("only-first");
                return ValueTask.CompletedTask;
            },
            options => options.BatchSize = 2);
        var batcher = provider.GetRequiredService<IRequestBatcher<int, string>>();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => batcher.ExecuteAsync([1, 2]));

        Assert.Contains("without assigning a response", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_HandlerSetsResponseTwice_FailsHandlerBatch()
    {
        await using var provider = CreateProvider<int, string>(
            (entries, _) =>
            {
                entries[0].SetResponse("first");
                entries[0].SetResponse("second");
                return ValueTask.CompletedTask;
            },
            options => options.BatchSize = 2);
        var batcher = provider.GetRequiredService<IRequestBatcher<int, string>>();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => batcher.ExecuteAsync([1, 2]));

        Assert.Contains("already been assigned", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SetResponse_SameItemTwice_PreservesFirstResponse()
    {
        var entry = new RequestBatchEntry<int, string>(1);

        entry.SetResponse("first");

        var exception = Assert.Throws<InvalidOperationException>(() => entry.SetResponse("second"));

        Assert.Contains("already been assigned", exception.Message, StringComparison.Ordinal);
        Assert.Equal("first", entry.GetResponse());
    }

    [Fact]
    public void SetResponses_EnumerableMatchesInputOrder_AssignsResponses()
    {
        var entries = CreateEntries();
        var enumerationCount = 0;

        IEnumerable<string> Responses()
        {
            enumerationCount++;
            yield return "first";
            yield return "second";
        }

        entries.SetResponses(Responses());

        Assert.Equal(1, enumerationCount);
        Assert.Equal("first", entries[0].GetResponse());
        Assert.Equal("second", entries[1].GetResponse());
    }

    [Fact]
    public void SetResponses_EnumerableHasDifferentCount_DoesNotAssignAnyResponse()
    {
        var entries = CreateEntries();

        var exception = Assert.Throws<ArgumentException>(() => entries.SetResponses(["first"]));

        Assert.Equal("responses", exception.ParamName);
        AssertResponseIsUnset(entries[0]);
        AssertResponseIsUnset(entries[1]);
    }

    [Fact]
    public void SetResponses_EnumerationFails_DoesNotAssignAnyResponse()
    {
        var entries = CreateEntries();
        var expected = new TestBatchException();

        IEnumerable<string> Responses()
        {
            yield return "first";
            throw expected;
        }

        var actual = Assert.Throws<TestBatchException>(() => entries.SetResponses(Responses()));

        Assert.Same(expected, actual);
        AssertResponseIsUnset(entries[0]);
        AssertResponseIsUnset(entries[1]);
    }

    [Fact]
    public void SetResponses_ResponseFactoryFails_DoesNotAssignAnyResponse()
    {
        var entries = CreateEntries();
        var expected = new TestBatchException();

        var actual = Assert.Throws<TestBatchException>(() => entries.SetResponses(request =>
            request == 1 ? "first" : throw expected));

        Assert.Same(expected, actual);
        AssertResponseIsUnset(entries[0]);
        AssertResponseIsUnset(entries[1]);
    }

    [Fact]
    public async Task AddRequestBatcher_ResponsePartitionKeySelectorFails_PropagatesOriginalException()
    {
        var expected = new TestBatchException();
        var invocationCount = 0;

        await using var provider = CreateProvider<int, string>(
            (entries, _) =>
            {
                Interlocked.Increment(ref invocationCount);
                entries.SetResponses(static request => request.ToString());
                return ValueTask.CompletedTask;
            },
            options => options.UsePartitionKey(
                request => request >= 0 ? request : throw expected,
                partitionCount: 2));
        var batcher = provider.GetRequiredService<IRequestBatcher<int, string>>();

        var actual = await Assert.ThrowsAsync<TestBatchException>(() => batcher.ExecuteAsync(-1));

        Assert.Same(expected, actual);
        Assert.Equal(0, Volatile.Read(ref invocationCount));
    }

    [Fact]
    public async Task AddRequestBatcher_ResponsePartitionKeySelector_UsesOriginalRequest()
    {
        var selectedKeys = new ConcurrentQueue<int>();

        await using var provider = CreateProvider<KeyedResponseRequest, string>(
            (entries, _) =>
            {
                entries.SetResponses(static request => request.Value.ToString());
                return ValueTask.CompletedTask;
            },
            options =>
            {
                options.BatchSize = 1;
                options.UsePartitionKey(request =>
                {
                    selectedKeys.Enqueue(request.Key);
                    return request.Key;
                }, partitionCount: 2);
            });
        var batcher = provider.GetRequiredService<IRequestBatcher<KeyedResponseRequest, string>>();

        var actual = await batcher.ExecuteAsync(
            [new KeyedResponseRequest(2, 20), new KeyedResponseRequest(1, 10), new KeyedResponseRequest(2, 21)]);

        Assert.Equal(new[] { "20", "10", "21" }, actual);
        Assert.Equal(new[] { 1, 2, 2 }, selectedKeys.Order());
    }

    [Fact]
    public void AddRequestBatcher_ResponseAndVoidForSameRequestType_RejectsDuplicatePipeline()
    {
        var services = new ServiceCollection();
        AddResponseHandler<int, string>(
            services,
            (entries, _) =>
            {
                entries.SetResponses(static request => request.ToString());
                return ValueTask.CompletedTask;
            });

        var exception = Assert.Throws<InvalidOperationException>(() =>
            services.AddRequestBatcher<int, NoResponseHandler>(ServiceLifetime.Singleton));

        Assert.Contains("already been registered", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddRequestBatcher_DuplicateResponseRegistration_DoesNotChangeExistingPipeline()
    {
        var services = new ServiceCollection();
        AddResponseHandler<int, string>(
            services,
            (entries, _) =>
            {
                entries.SetResponses(static request => request.ToString());
                return ValueTask.CompletedTask;
            },
            configure: options =>
            {
                options.BatchSize = 32;
                options.MaxConcurrency = 4;
                options.MaxPendingRequests = 512;
                options.FullMode = RequestBatchFullMode.Fail;
            });
        var descriptorCount = services.Count;
        var duplicateConfigureCallCount = 0;

        var exception = Assert.Throws<InvalidOperationException>(() =>
            services.AddRequestBatcher<int, string, DuplicateResponseHandler>(
                ServiceLifetime.Singleton,
                options =>
                {
                    duplicateConfigureCallCount++;
                    options.BatchSize = 1;
                }));

        Assert.Contains("already been registered", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, duplicateConfigureCallCount);
        Assert.Equal(descriptorCount, services.Count);

        using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        var options = provider.GetRequiredService<IOptions<RequestBatchOptions<int>>>().Value;

        Assert.Equal(32, options.BatchSize);
        Assert.Equal(4, options.MaxConcurrency);
        Assert.Equal(512, options.MaxPendingRequests);
        Assert.Equal(RequestBatchFullMode.Fail, options.FullMode);
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

            Assert.Equal("1", await batcher.ExecuteAsync(1).WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal("2", await batcher.ExecuteAsync(2).WaitAsync(TimeSpan.FromSeconds(5)));
        }

        Assert.Equal(2, probe.HandlerIds.Distinct().Count());
        Assert.Equal(2, probe.DisposeCount);
    }

    [Fact]
    public void AddRequestBatcher_ResponseConfigure_RegistersPublicOptionsAndInvokesCallbackOnce()
    {
        var configureCallCount = 0;
        var services = new ServiceCollection();
        AddResponseHandler<int, string>(
            services,
            (entries, _) =>
            {
                entries.SetResponses(static request => request.ToString());
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
    public async Task ExecuteAsync_RequestCanceledBeforeDispatch_SkipsResponseHandler()
    {
        var firstHandlerStarted = NewSource();
        var releaseFirstHandler = NewSource();
        var handledRequests = new ConcurrentQueue<int>();

        await using var provider = CreateProvider<int, string>(
            async (entries, _) =>
            {
                var request = Assert.Single(entries).Request;
                handledRequests.Enqueue(request);
                if (request == 1)
                {
                    firstHandlerStarted.SetResult();
                    await releaseFirstHandler.Task;
                }

                entries[0].SetResponse(request.ToString());
            },
            options =>
            {
                options.BatchSize = 1;
                options.MaxPendingRequests = 1;
            });
        var batcher = provider.GetRequiredService<IRequestBatcher<int, string>>();

        var first = batcher.ExecuteAsync(1);
        await firstHandlerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var cancellation = new CancellationTokenSource();
        var canceled = batcher.ExecuteAsync(2, cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);
        releaseFirstHandler.SetResult();

        Assert.Equal("1", await first.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(new[] { 1 }, handledRequests);
    }

    [Fact]
    public async Task DisposeAsync_ResponseContainerDisposal_DrainsAcceptedRequestsAndRejectsNewRequests()
    {
        var firstHandlerStarted = NewSource();
        var releaseFirstHandler = NewSource();

        await using var provider = CreateProvider<int, string>(
            async (entries, _) =>
            {
                var request = Assert.Single(entries).Request;
                if (request == 1)
                {
                    firstHandlerStarted.SetResult();
                    await releaseFirstHandler.Task;
                }

                entries[0].SetResponse(request.ToString());
            },
            options => options.BatchSize = 1);
        var batcher = provider.GetRequiredService<IRequestBatcher<int, string>>();

        var first = batcher.ExecuteAsync(1);
        await firstHandlerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = batcher.ExecuteAsync(2);
        var stopping = provider.DisposeAsync().AsTask();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => batcher.ExecuteAsync(3));
        releaseFirstHandler.SetResult();

        Assert.Equal(new[] { "1", "2" }, await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5)));
        await stopping.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static void AddResponseHandler<TRequest, TResponse>(
        IServiceCollection services,
        Func<IReadOnlyList<RequestBatchEntry<TRequest, TResponse>>, CancellationToken, ValueTask> handler,
        ServiceLifetime handlerLifetime = ServiceLifetime.Singleton,
        Action<RequestBatchOptions<TRequest>>? configure = null)
    {
        services.AddSingleton(handler);
        services.AddRequestBatcher<TRequest, TResponse, TestResponseHandler<TRequest, TResponse>>(
            handlerLifetime,
            configure);
    }

    private static ServiceProvider CreateProvider<TRequest, TResponse>(
        Func<IReadOnlyList<RequestBatchEntry<TRequest, TResponse>>, CancellationToken, ValueTask> handler,
        Action<RequestBatchOptions<TRequest>>? configure = null)
    {
        var services = new ServiceCollection();
        AddResponseHandler(services, handler, ServiceLifetime.Singleton, configure);
        return services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    private static RequestBatchEntry<int, string>[] CreateEntries() =>
    [
        new RequestBatchEntry<int, string>(1),
        new RequestBatchEntry<int, string>(2),
    ];

    private static void AssertResponseIsUnset(RequestBatchEntry<int, string> entry) =>
        Assert.Throws<InvalidOperationException>(() => entry.GetResponse());

    private static TaskCompletionSource NewSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public sealed record ResponseRequest(int Value);

    private sealed record KeyedResponseRequest(int Key, int Value);

    private sealed class TestBatchException : Exception;

    private sealed class TestResponseHandler<TRequest, TResponse>(
        Func<IReadOnlyList<RequestBatchEntry<TRequest, TResponse>>, CancellationToken, ValueTask> handler)
        : IRequestBatchHandler<TRequest, TResponse>
    {
        public ValueTask HandleAsync(
            IReadOnlyList<RequestBatchEntry<TRequest, TResponse>> entries,
            CancellationToken cancellationToken = default) =>
            handler(entries, cancellationToken);
    }

    private sealed class NoResponseHandler : IRequestBatchHandler<int>
    {
        public ValueTask HandleAsync(
            IReadOnlyList<int> requests,
            CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
    }

    private sealed class DuplicateResponseHandler : IRequestBatchHandler<int, string>
    {
        public ValueTask HandleAsync(
            IReadOnlyList<RequestBatchEntry<int, string>> entries,
            CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
    }

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
            IReadOnlyList<RequestBatchEntry<int, string>> entries,
            CancellationToken cancellationToken = default)
        {
            probe.HandlerIds.Enqueue(_id);
            entries.SetResponses(static request => request.ToString());
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            probe.RecordDisposed();
            return ValueTask.CompletedTask;
        }
    }
}
