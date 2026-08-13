using BufferQueue;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace RequestBatcher;

/// <summary>
/// Registration helpers that compose RequestBatcher into an externally owned dependency injection container.
/// </summary>
public static class RequestBatcherServiceCollectionExtensions
{
    /// <summary>
    /// Registers one singleton request batcher and its handler for <typeparamref name="TRequest"/>.
    /// </summary>
    /// <param name="services">The application-owned service collection.</param>
    /// <param name="handlerLifetime">The handler lifetime. Scoped and transient handlers are resolved once per batch.</param>
    /// <param name="configure">An optional callback that configures batching.</param>
    public static IServiceCollection AddRequestBatcher<TRequest, THandler>(
        this IServiceCollection services,
        ServiceLifetime handlerLifetime,
        Action<RequestBatchOptions<TRequest>>? configure = null)
        where THandler : class, IRequestBatchHandler<TRequest>
    {
        ArgumentNullException.ThrowIfNull(services);
        ValidateHandlerLifetime(handlerLifetime);

        return AddRequestBatcherCore<TRequest>(
            services,
            handlerLifetime,
            configure,
            services => services.Add(new ServiceDescriptor(
                typeof(IRequestBatchHandler<TRequest>),
                typeof(THandler),
                handlerLifetime)));
    }

    /// <summary>
    /// Registers one singleton request batcher backed by a handler delegate.
    /// </summary>
    /// <param name="services">The application-owned service collection.</param>
    /// <param name="handler">The function that processes one batch.</param>
    /// <param name="handlerLifetime">The handler lifetime. Scoped and transient handlers are resolved once per batch.</param>
    /// <param name="configure">An optional callback that configures batching.</param>
    public static IServiceCollection AddRequestBatcher<TRequest>(
        this IServiceCollection services,
        RequestBatchHandler<TRequest> handler,
        ServiceLifetime handlerLifetime,
        Action<RequestBatchOptions<TRequest>>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(handler);
        ValidateHandlerLifetime(handlerLifetime);

        return AddRequestBatcherCore<TRequest>(
            services,
            handlerLifetime,
            configure,
            services => services.Add(new ServiceDescriptor(
                typeof(IRequestBatchHandler<TRequest>),
                _ => new DelegateRequestBatchHandler<TRequest>(handler),
                handlerLifetime)));
    }

    private static IServiceCollection AddRequestBatcherCore<TRequest>(
        IServiceCollection services,
        ServiceLifetime handlerLifetime,
        Action<RequestBatchOptions<TRequest>>? configure,
        Action<IServiceCollection> registerHandler)
    {
        if (services.Any(descriptor => descriptor.ServiceType == typeof(IRequestBatcher<TRequest>)))
        {
            throw new InvalidOperationException(
                $"A request batcher for '{typeof(TRequest)}' has already been registered.");
        }

        var options = new RequestBatchOptions<TRequest>();
        configure?.Invoke(options);
        options = options.ValidateAndClone();

        registerHandler(services);
        AddInternalBufferQueueTopic<TRequest>(services, options);
        services.AddSingleton<RequestBatchCoordinator<TRequest>>(provider =>
            new RequestBatchCoordinator<TRequest>(
                provider.GetRequiredService<IBufferQueue>(),
                CreateHandler<TRequest>(provider, handlerLifetime),
                options,
                provider.GetService<ILogger<RequestBatchCoordinator<TRequest>>>()));
        services.AddSingleton<IRequestBatcher<TRequest>>(
            static provider => provider.GetRequiredService<RequestBatchCoordinator<TRequest>>());

        return services;
    }

    private static void AddInternalBufferQueueTopic<TRequest>(
        IServiceCollection services,
        RequestBatchOptions<TRequest> options)
    {
        if (!services.Any(descriptor =>
                descriptor.ServiceType == typeof(IBufferQueue) && !descriptor.IsKeyedService))
        {
            services.AddBufferQueue(static _ => { });
        }

        new BufferOptionsBuilder(services).UseMemory(memory =>
            memory.AddTopic<PendingBatchRequest<TRequest>>(topic =>
            {
                topic.TopicName = RequestBatchCoordinator<TRequest>.TopicName;
                topic.PartitionNumber = options.MaxConcurrency;
                topic.SegmentSize = Math.Max(16, options.BatchSize);
                options.ConfigurePartitionKey?.Invoke(topic);
            }));
    }

    private static RequestBatchHandler<TRequest> CreateHandler<TRequest>(
        IServiceProvider provider,
        ServiceLifetime handlerLifetime)
    {
        if (handlerLifetime == ServiceLifetime.Singleton)
        {
            return provider.GetRequiredService<IRequestBatchHandler<TRequest>>().HandleAsync;
        }

        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
        return HandleInScopeAsync;

        async ValueTask HandleInScopeAsync(
            IReadOnlyList<TRequest> requests,
            CancellationToken cancellationToken)
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var handler = scope.ServiceProvider.GetRequiredService<IRequestBatchHandler<TRequest>>();
            await handler.HandleAsync(requests, cancellationToken).ConfigureAwait(false);
        }
    }

    private static void ValidateHandlerLifetime(ServiceLifetime handlerLifetime)
    {
        if (handlerLifetime is not ServiceLifetime.Singleton and
            not ServiceLifetime.Scoped and
            not ServiceLifetime.Transient)
        {
            throw new ArgumentOutOfRangeException(nameof(handlerLifetime));
        }
    }

    private sealed class DelegateRequestBatchHandler<TRequest>(RequestBatchHandler<TRequest> handler)
        : IRequestBatchHandler<TRequest>
    {
        public ValueTask HandleAsync(
            IReadOnlyList<TRequest> requests,
            CancellationToken cancellationToken = default) =>
            handler(requests, cancellationToken);
    }
}
