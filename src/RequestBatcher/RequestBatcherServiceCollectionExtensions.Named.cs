using BufferQueue;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace RequestBatcher;

public static partial class RequestBatcherServiceCollectionExtensions
{
    /// <summary>
    /// Registers an independent named singleton request batcher and its handler.
    /// </summary>
    /// <remarks>
    /// Resolve the batcher as a keyed service using <paramref name="batcherName"/>.
    /// Its registration-time configuration is available through named options with the same name.
    /// </remarks>
    /// <typeparam name="TRequest">The request type.</typeparam>
    /// <typeparam name="THandler">The handler type.</typeparam>
    /// <param name="services">The application-owned service collection.</param>
    /// <param name="batcherName">The non-empty, case-sensitive batcher name.</param>
    /// <param name="handlerLifetime">The handler lifetime. Scoped and transient handlers are resolved once per batch.</param>
    /// <param name="configure">An optional callback that configures this pipeline.</param>
    /// <returns>The service collection.</returns>
    public static IServiceCollection AddRequestBatcher<TRequest, THandler>(
        this IServiceCollection services,
        string batcherName,
        ServiceLifetime handlerLifetime,
        Action<RequestBatchOptions<TRequest>>? configure = null)
        where THandler : class, IRequestBatchHandler<TRequest>
    {
        ValidateNamedRegistration<TRequest>(services, batcherName, handlerLifetime);
        var options = CreateNamedOptions(configure);

        RegisterNamedPipeline<TRequest>(services, batcherName);
        services.Add(new ServiceDescriptor(
            typeof(IRequestBatchHandler<TRequest>), batcherName, typeof(THandler), handlerLifetime));
        AddNamedOptions(services, batcherName, options);
        AddKeyedCoordinator<TRequest>(services, batcherName, handlerLifetime, options);
        services.AddKeyedSingleton<IRequestBatcher<TRequest>>(
            batcherName,
            (provider, key) => provider.GetRequiredKeyedService<RequestBatchCoordinator<TRequest>>(key));

        return services;
    }

    /// <summary>
    /// Registers an independent named singleton request/response batcher and its handler.
    /// </summary>
    /// <remarks>
    /// Resolve the batcher as a keyed service using <paramref name="batcherName"/>.
    /// Its registration-time configuration is available through named options with the same name.
    /// </remarks>
    /// <typeparam name="TRequest">The request type.</typeparam>
    /// <typeparam name="TResponse">The response type.</typeparam>
    /// <typeparam name="THandler">The response-bearing handler type.</typeparam>
    /// <param name="services">The application-owned service collection.</param>
    /// <param name="batcherName">The non-empty, case-sensitive batcher name.</param>
    /// <param name="handlerLifetime">The handler lifetime. Scoped and transient handlers are resolved once per batch.</param>
    /// <param name="configure">An optional callback that configures this pipeline.</param>
    /// <returns>The service collection.</returns>
    public static IServiceCollection AddRequestBatcher<TRequest, TResponse, THandler>(
        this IServiceCollection services,
        string batcherName,
        ServiceLifetime handlerLifetime,
        Action<RequestBatchOptions<TRequest>>? configure = null)
        where THandler : class, IRequestBatchHandler<TRequest, TResponse>
    {
        ValidateNamedRegistration<TRequest>(services, batcherName, handlerLifetime);
        var options = CreateNamedOptions(configure);
        var entryOptions = options.Project<RequestBatchEntry<TRequest, TResponse>>(static entry => entry.Request);
        var pipelineKey = new object();

        RegisterNamedPipeline<TRequest>(services, batcherName);
        services.Add(new ServiceDescriptor(
            typeof(IRequestBatchHandler<TRequest, TResponse>), batcherName, typeof(THandler), handlerLifetime));
        AddNamedOptions(services, batcherName, options);
        services.Add(new ServiceDescriptor(
            typeof(IRequestBatchHandler<RequestBatchEntry<TRequest, TResponse>>),
            pipelineKey,
            (provider, _) => new ResponseRequestBatchHandler<TRequest, TResponse>(
                provider.GetRequiredKeyedService<IRequestBatchHandler<TRequest, TResponse>>(batcherName)),
            handlerLifetime));
        AddKeyedCoordinator<RequestBatchEntry<TRequest, TResponse>>(
            services, pipelineKey, handlerLifetime, entryOptions);
        services.AddKeyedSingleton<IRequestBatcher<TRequest, TResponse>>(
            batcherName,
            (provider, _) => new ResponseRequestBatcher<TRequest, TResponse>(
                provider.GetRequiredKeyedService<RequestBatchCoordinator<RequestBatchEntry<TRequest, TResponse>>>(
                    pipelineKey)));

        return services;
    }

    private static void ValidateNamedRegistration<TRequest>(
        IServiceCollection services,
        string batcherName,
        ServiceLifetime handlerLifetime)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(batcherName);

        ValidateHandlerLifetime(handlerLifetime);
        if (services.Any(descriptor =>
                descriptor.ServiceType == typeof(RequestBatcherRegistration<TRequest>) &&
                descriptor.IsKeyedService && Equals(descriptor.ServiceKey, batcherName)) ||
            services.Any(descriptor =>
                descriptor.IsKeyedService && Equals(descriptor.ServiceKey, batcherName) &&
                (descriptor.ServiceType == typeof(IRequestBatcher<TRequest>) ||
                 descriptor.ServiceType == typeof(RequestBatchCoordinator<TRequest>))))
        {
            throw new InvalidOperationException(
                $"A request batcher for '{typeof(TRequest)}' named '{batcherName}' has already been registered.");
        }
    }

    private static RequestBatchOptions<TRequest> CreateNamedOptions<TRequest>(
        Action<RequestBatchOptions<TRequest>>? configure)
    {
        var options = new RequestBatchOptions<TRequest>();
        configure?.Invoke(options);
        return options.ValidateAndClone();
    }

    private static void RegisterNamedPipeline<TRequest>(IServiceCollection services, string batcherName)
    {
        services.AddLogging();
        services.AddKeyedSingleton<RequestBatcherRegistration<TRequest>>(batcherName);
    }

    private static void AddNamedOptions<TRequest>(
        IServiceCollection services,
        string batcherName,
        RequestBatchOptions<TRequest> options) =>
        services.AddOptions<RequestBatchOptions<TRequest>>(batcherName)
            .Configure(configured => configured.CopyFrom(options));

    private static void AddKeyedCoordinator<TRequest>(
        IServiceCollection services,
        object serviceKey,
        ServiceLifetime handlerLifetime,
        RequestBatchOptions<TRequest> options)
    {
        var topicName = $"request-batcher-{Guid.NewGuid():N}";
        AddInternalBufferQueueTopic(services, options, topicName);
        services.AddKeyedSingleton<RequestBatchCoordinator<TRequest>>(
            serviceKey,
            (provider, key) => new RequestBatchCoordinator<TRequest>(
                provider.GetRequiredService<IBufferQueue>(),
                CreateKeyedHandler<TRequest>(provider, key, handlerLifetime),
                Options.Create(options),
                provider.GetRequiredService<ILogger<RequestBatchCoordinator<TRequest>>>(),
                topicName));
    }

    private static RequestBatchHandler<TRequest> CreateKeyedHandler<TRequest>(
        IServiceProvider provider,
        object? serviceKey,
        ServiceLifetime handlerLifetime)
    {
        if (handlerLifetime == ServiceLifetime.Singleton)
        {
            return provider.GetRequiredKeyedService<IRequestBatchHandler<TRequest>>(serviceKey).HandleAsync;
        }

        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
        return HandleInScopeAsync;

        async ValueTask HandleInScopeAsync(IReadOnlyList<TRequest> requests, CancellationToken cancellationToken)
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var handler = scope.ServiceProvider.GetRequiredKeyedService<IRequestBatchHandler<TRequest>>(serviceKey);
            await handler.HandleAsync(requests, cancellationToken).ConfigureAwait(false);
        }
    }
}
