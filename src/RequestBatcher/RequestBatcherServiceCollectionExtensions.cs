using BufferQueue;
using BufferQueue.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RequestBatcher.PendingRequests;

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
        RegisterBatcherPipeline<TRequest>(services);

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
    /// Registers one singleton request/response batcher and its handler for <typeparamref name="TRequest"/>.
    /// </summary>
    /// <param name="services">The application-owned service collection.</param>
    /// <param name="handlerLifetime">The handler lifetime. Scoped and transient handlers are resolved once per batch.</param>
    /// <param name="configure">An optional callback that configures batching.</param>
    /// <typeparam name="TRequest">The request type.</typeparam>
    /// <typeparam name="TResponse">The response type.</typeparam>
    /// <typeparam name="THandler">The response-bearing handler type.</typeparam>
    public static IServiceCollection AddRequestBatcher<TRequest, TResponse, THandler>(
        this IServiceCollection services,
        ServiceLifetime handlerLifetime,
        Action<RequestBatchOptions<TRequest>>? configure = null)
        where THandler : class, IRequestBatchHandler<TRequest, TResponse>
    {
        ArgumentNullException.ThrowIfNull(services);
        ValidateHandlerLifetime(handlerLifetime);

        return AddResponseBatcherCore<TRequest, TResponse>(
            services,
            handlerLifetime,
            configure,
            services => services.Add(new ServiceDescriptor(
                typeof(IRequestBatchHandler<TRequest, TResponse>),
                typeof(THandler),
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

        services.AddLogging();
        var optionsSnapshot = ConfigureOptions(services, configure);

        registerHandler(services);
        AddInternalBufferQueueTopic<TRequest>(services, optionsSnapshot);
        services.AddSingleton<RequestBatchCoordinator<TRequest>>(provider =>
            new RequestBatchCoordinator<TRequest>(
                provider.GetRequiredService<IBufferQueue>(),
                CreateHandler<TRequest>(provider, handlerLifetime),
                provider.GetRequiredService<IOptions<RequestBatchOptions<TRequest>>>(),
                provider.GetRequiredService<ILogger<RequestBatchCoordinator<TRequest>>>()));
        services.AddSingleton<IRequestBatcher<TRequest>>(
            static provider => provider.GetRequiredService<RequestBatchCoordinator<TRequest>>());

        return services;
    }

    private static IServiceCollection AddResponseBatcherCore<TRequest, TResponse>(
        IServiceCollection services,
        ServiceLifetime handlerLifetime,
        Action<RequestBatchOptions<TRequest>>? configure,
        Action<IServiceCollection> registerHandler)
    {
        var configuredOptions = ConfigureOptions(services, configure);
        var itemOptions = configuredOptions
            .Project<RequestBatchItem<TRequest, TResponse>>(static item => item.Request);

        RegisterBatcherPipeline<TRequest>(services);
        registerHandler(services);
        AddRequestBatcherCore<RequestBatchItem<TRequest, TResponse>>(
            services,
            handlerLifetime,
            options => options.CopyFrom(itemOptions),
            services => services.Add(new ServiceDescriptor(
                typeof(IRequestBatchHandler<RequestBatchItem<TRequest, TResponse>>),
                provider => new ResponseRequestBatchHandler<TRequest, TResponse>(
                    provider.GetRequiredService<IRequestBatchHandler<TRequest, TResponse>>()),
                handlerLifetime)));
        services.AddSingleton<IRequestBatcher<TRequest, TResponse>>(
            provider => new ResponseRequestBatcher<TRequest, TResponse>(
                provider.GetRequiredService<IRequestBatcher<RequestBatchItem<TRequest, TResponse>>>()));

        return services;
    }

    private static RequestBatchOptions<TRequest> ConfigureOptions<TRequest>(
        IServiceCollection services,
        Action<RequestBatchOptions<TRequest>>? configure)
    {
        // BufferQueue materializes topic topology during service registration, so both layers share one snapshot.
        var configuredOptions = new RequestBatchOptions<TRequest>();
        configure?.Invoke(configuredOptions);
        var optionsSnapshot = configuredOptions.ValidateAndClone();

        services
            .AddOptions<RequestBatchOptions<TRequest>>()
            .Configure(options => options.CopyFrom(optionsSnapshot))
            .Validate(
                static options => options.BatchSize > 0,
                "Batch size must be greater than zero.")
            .Validate(
                static options => options.MaxConcurrency > 0,
                "Maximum concurrency must be greater than zero.")
            .Validate(
                static options => options.MaxPendingRequests > 0,
                "Maximum pending requests must be greater than zero.")
            .Validate(
                static options => Enum.IsDefined(options.FullMode),
                "Unknown full mode.");

        return optionsSnapshot;
    }

    private static void AddInternalBufferQueueTopic<TRequest>(
        IServiceCollection services,
        RequestBatchOptions<TRequest> options)
    {
        services.AddBufferQueue(queue =>
            queue.UseMemory(memory =>
                memory.AddTopic<PendingBatchRequest<TRequest>>(topic =>
                {
                    topic.TopicName = RequestBatchCoordinator<TRequest>.TopicName;
                    topic.PartitionNumber = GetPartitionCount(
                        options.MaxConcurrency,
                        Environment.ProcessorCount);
                    topic.SegmentSize = Math.Max(16, options.BatchSize);
                    topic.BoundedCapacity = (ulong)options.MaxPendingRequests;
                    topic.FullMode = options.FullMode switch
                    {
                        RequestBatchFullMode.Wait => BufferQueueFullMode.Wait,
                        RequestBatchFullMode.Fail => BufferQueueFullMode.Fail,
                        _ => throw new ArgumentOutOfRangeException(nameof(options.FullMode)),
                    };
                    options.PartitionKey?.Configure(topic);
                })));
    }

    private static void RegisterBatcherPipeline<TRequest>(IServiceCollection services)
    {
        if (services.Any(descriptor => descriptor.ServiceType == typeof(RequestBatcherRegistration<TRequest>)))
        {
            throw new InvalidOperationException(
                $"A request batcher for '{typeof(TRequest)}' has already been registered.");
        }

        services.AddSingleton<RequestBatcherRegistration<TRequest>>();
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

    internal static int GetPartitionCount(int maxConcurrency, int processorCount) =>
        Math.Min(maxConcurrency, Math.Max(1, processorCount));

    private static void ValidateHandlerLifetime(ServiceLifetime handlerLifetime)
    {
        if (handlerLifetime is not ServiceLifetime.Singleton and
            not ServiceLifetime.Scoped and
            not ServiceLifetime.Transient)
        {
            throw new ArgumentOutOfRangeException(nameof(handlerLifetime));
        }
    }

}
