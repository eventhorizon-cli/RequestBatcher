using Microsoft.Extensions.DependencyInjection;
using Moq;
using RequestBatcher.DelegateHandlers;

namespace RequestBatcher.Tests;

public sealed class DelegateRequestBatchHandlerLifetimeTests
{
    [Fact]
    public async Task DelegateRequestBatchHandler_NoServices_SkipsScopeCreation()
    {
        var scopeFactory = new Mock<IServiceScopeFactory>(MockBehavior.Strict);
        var handler = new DelegateRequestBatchHandler<int>(scopeFactory.Object);
        using var cancellation = new CancellationTokenSource();

        handler.Bind(static (IReadOnlyList<int> requests, CancellationToken cancellationToken) =>
        {
            Assert.Single(requests);
            Assert.True(cancellationToken.CanBeCanceled);
        });
        await handler.HandleAsync([1], cancellation.Token);
        await handler.HandleAsync([2], cancellation.Token);

        scopeFactory.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task DelegateRequestBatchHandler_ServiceLifetimes_IsolateBatches()
    {
        var probe = new LifetimeProbe();
        var services = new ServiceCollection();
        services.AddSingleton(probe);
        services.AddSingleton<SingletonDependency>();
        services.AddScoped<ScopedDependency>();
        services.AddTransient<TransientDependency>();
        services.AddDelegateRequestBatcher<int>(options => options.BatchSize = 1);
        await using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true });
        var batcher = provider.GetRequiredService<IRequestBatcher<int>>();

        await ExecuteWithLifetimesAsync(batcher, 1);
        await ExecuteWithLifetimesAsync(batcher, 2);

        var first = Assert.Single(probe.Invocations, static invocation => invocation.Request == 1);
        var second = Assert.Single(probe.Invocations, static invocation => invocation.Request == 2);

        Assert.Same(first.SingletonFirst, first.SingletonSecond);
        Assert.Same(first.SingletonFirst, second.SingletonFirst);
        Assert.Same(first.ScopedFirst, first.ScopedSecond);
        Assert.NotSame(first.ScopedFirst, second.ScopedFirst);
        Assert.NotSame(first.TransientFirst, first.TransientSecond);
        Assert.NotSame(first.TransientFirst, second.TransientFirst);
        Assert.True(first.ScopedFirst.IsDisposed);
        Assert.True(first.ScopedSecond.IsDisposed);
        Assert.True(first.TransientFirst.IsDisposed);
        Assert.True(first.TransientSecond.IsDisposed);
        Assert.True(second.ScopedFirst.IsDisposed);
        Assert.True(second.ScopedSecond.IsDisposed);
        Assert.True(second.TransientFirst.IsDisposed);
        Assert.True(second.TransientSecond.IsDisposed);
        Assert.False(first.SingletonFirst.IsDisposed);

        await provider.DisposeAsync();

        Assert.True(first.SingletonFirst.IsDisposed);
        Assert.Equal(1, first.SingletonFirst.DisposeCount);
    }

    [Fact]
    public void AddDelegateRequestBatcher_HandlerDescriptors_AreSingleton()
    {
        var services = new ServiceCollection();
        services.AddDelegateRequestBatcher<int>();
        services.AddDelegateRequestBatcher<int>("named");
        services.AddDelegateRequestBatcher<string, string>();
        services.AddDelegateRequestBatcher<string, string>("named-response");

        var descriptors = services
            .Where(static descriptor =>
                descriptor.ServiceType.IsGenericType &&
                descriptor.ServiceType.GetGenericTypeDefinition() == typeof(DelegateRequestBatchHandler<>))
            .ToArray();

        Assert.Equal(4, descriptors.Length);
        Assert.All(descriptors, static descriptor => Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime));
    }

    private static Task ExecuteWithLifetimesAsync(IRequestBatcher<int> batcher, int request) =>
        batcher.ExecuteAsync(
            request,
            static (
                IReadOnlyList<int> requests,
                LifetimeProbe lifetimeProbe,
                SingletonDependency singletonFirst,
                SingletonDependency singletonSecond,
                ScopedDependency scopedFirst,
                ScopedDependency scopedSecond,
                TransientDependency transientFirst,
                TransientDependency transientSecond) =>
            {
                lifetimeProbe.Invocations.Add(
                    new Invocation(
                        requests.Single(),
                        singletonFirst,
                        singletonSecond,
                        scopedFirst,
                        scopedSecond,
                        transientFirst,
                        transientSecond));
            });

    private sealed class LifetimeProbe
    {
        public List<Invocation> Invocations { get; } = [];
    }

    private sealed record Invocation(
        int Request,
        SingletonDependency SingletonFirst,
        SingletonDependency SingletonSecond,
        ScopedDependency ScopedFirst,
        ScopedDependency ScopedSecond,
        TransientDependency TransientFirst,
        TransientDependency TransientSecond);

    private sealed class SingletonDependency : IAsyncDisposable
    {
        public bool IsDisposed { get; private set; }

        public int DisposeCount { get; private set; }

        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ScopedDependency : IAsyncDisposable
    {
        public bool IsDisposed { get; private set; }

        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TransientDependency : IAsyncDisposable
    {
        public bool IsDisposed { get; private set; }

        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
