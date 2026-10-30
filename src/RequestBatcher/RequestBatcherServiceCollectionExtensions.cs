using BufferQueue;
using BufferQueue.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RequestBatcher.Diagnostics.Metrics;
using RequestBatcher.Handlers;
using RequestBatcher.PendingRequests;

namespace RequestBatcher;

/// <summary>
/// Registration helpers that compose RequestBatcher into an externally owned dependency injection container.
/// </summary>
public static partial class RequestBatcherServiceCollectionExtensions
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
        RegisterDefaultBatcher<TRequest>(services);

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
        if (services.Any(descriptor =>
                descriptor.ServiceType == typeof(IRequestBatcher<TRequest>) && !descriptor.IsKeyedService))
        {
            throw new InvalidOperationException(
                $"A request batcher for '{typeof(TRequest)}' has already been registered.");
        }

        services.AddLogging();
        var optionsSnapshot = ConfigureOptions(services, configure);

        registerHandler(services);
        AddBatcherServices<TRequest>(services, optionsSnapshot);
        services.AddSingleton(new RequestBatcherConfiguration<TRequest>(
            RequestBatchCoordinator<TRequest>.TopicName, null));
        RegisterHandlerDelegate<TRequest>(services, handlerLifetime);
        services.AddSingleton<RequestBatchCoordinator<TRequest>>();
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
        RegisterDefaultBatcher<TRequest>(services);

        var configuredOptions = ConfigureOptions(services, configure);
        var entryOptions = configuredOptions
            .Project<RequestBatchEntry<TRequest, TResponse>>(static entry => entry.Request);

        registerHandler(services);
        AddRequestBatcherCore<RequestBatchEntry<TRequest, TResponse>>(
            services,
            handlerLifetime,
            options => options.CopyFrom(entryOptions),
            services => services.Add(new ServiceDescriptor(
                typeof(IRequestBatchHandler<RequestBatchEntry<TRequest, TResponse>>),
                typeof(ResponseRequestBatchHandler<TRequest, TResponse>),
                handlerLifetime)));
        services.AddSingleton<IRequestBatcher<TRequest, TResponse>, ResponseRequestBatcher<TRequest, TResponse>>();

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

    private static void AddBatcherServices<TRequest>(
        IServiceCollection services,
        RequestBatchOptions<TRequest> options,
        string topicName = RequestBatchCoordinator<TRequest>.TopicName)
    {
        services.AddSingleton(new RequestBatcherMetricsConfiguration(options.BatchSize));
        services.TryAddSingleton<RequestBatcherMetrics>();
        services.AddBufferQueue(queue =>
            queue.UseMemory(memory =>
                memory.AddTopic<PendingBatchRequest<TRequest>>(topic =>
                {
                    topic.TopicName = topicName;
                    topic.PartitionNumber = GetPartitionCount(
                        options.PartitionKey?.PartitionCount ?? 1,
                        options.PartitionKey is not null);
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

    private static void RegisterDefaultBatcher<TRequest>(IServiceCollection services)
    {
        if (services.Any(descriptor =>
                descriptor.ServiceType == typeof(RequestBatcherRegistration<TRequest>) && !descriptor.IsKeyedService))
        {
            throw new InvalidOperationException(
                $"A request batcher for '{typeof(TRequest)}' has already been registered.");
        }

        services.AddSingleton<RequestBatcherRegistration<TRequest>>();
    }

    private static void RegisterHandlerDelegate<TRequest>(
        IServiceCollection services,
        ServiceLifetime handlerLifetime,
        object? serviceKey = null)
    {
        if (serviceKey is null)
        {
            if (handlerLifetime == ServiceLifetime.Singleton)
            {
                services.AddSingleton<RequestBatchHandler<TRequest>>(
                    static provider => provider.GetRequiredService<IRequestBatchHandler<TRequest>>().HandleAsync);
            }
            else
            {
                services.AddSingleton<ScopedRequestBatchHandler<TRequest>>();
                services.AddSingleton<RequestBatchHandler<TRequest>>(
                    static provider => provider.GetRequiredService<ScopedRequestBatchHandler<TRequest>>().HandleAsync);
            }
        }
        else if (handlerLifetime == ServiceLifetime.Singleton)
        {
            services.AddKeyedSingleton<RequestBatchHandler<TRequest>>(serviceKey,
                static (provider, key) => provider.GetRequiredKeyedService<IRequestBatchHandler<TRequest>>(key).HandleAsync);
        }
        else
        {
            services.AddKeyedSingleton<ScopedRequestBatchHandler<TRequest>>(serviceKey);
            services.AddKeyedSingleton<RequestBatchHandler<TRequest>>(serviceKey,
                static (provider, key) => provider.GetRequiredKeyedService<ScopedRequestBatchHandler<TRequest>>(key).HandleAsync);
        }
    }

    internal static int GetPartitionCount(int configuredPartitionCount, bool hasPartitionKey) =>
        hasPartitionKey ? configuredPartitionCount : 1;

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
