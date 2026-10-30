using Microsoft.Extensions.DependencyInjection;
using RequestBatcher.Handlers;

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
    /// <param name="configure">An optional callback that configures this batcher.</param>
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

        RegisterNamedBatcher<TRequest>(services, batcherName);
        services.Add(new ServiceDescriptor(
            typeof(IRequestBatchHandler<TRequest>), batcherName, typeof(THandler), handlerLifetime));
        AddNamedOptions(services, batcherName, options);
        AddKeyedCoordinator<TRequest>(services, batcherName, handlerLifetime, options, batcherName);
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
    /// <param name="configure">An optional callback that configures this batcher.</param>
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
        var batcherKey = new object();

        RegisterNamedBatcher<TRequest>(services, batcherName);
        services.Add(new ServiceDescriptor(
            typeof(IRequestBatchHandler<TRequest, TResponse>), batcherName, typeof(THandler), handlerLifetime));
        AddNamedOptions(services, batcherName, options);
        services.Add(new ServiceDescriptor(
            typeof(IRequestBatchHandler<RequestBatchEntry<TRequest, TResponse>>),
            batcherKey,
            (provider, _) => new ResponseRequestBatchHandler<TRequest, TResponse>(
                provider.GetRequiredKeyedService<IRequestBatchHandler<TRequest, TResponse>>(batcherName)),
            handlerLifetime));
        AddKeyedCoordinator<RequestBatchEntry<TRequest, TResponse>>(
            services, batcherKey, handlerLifetime, entryOptions, batcherName);
        services.AddKeyedSingleton<ResponseRequestBatcher<TRequest, TResponse>>(batcherKey);
        services.AddKeyedSingleton<IRequestBatcher<TRequest, TResponse>>(
            batcherName,
            (provider, _) => provider.GetRequiredKeyedService<ResponseRequestBatcher<TRequest, TResponse>>(batcherKey));

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

    private static void RegisterNamedBatcher<TRequest>(IServiceCollection services, string batcherName)
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
        RequestBatchOptions<TRequest> options,
        string? batcherName)
    {
        var topicName = $"request-batcher-{Guid.NewGuid():N}";
        AddBatcherServices(services, options, topicName);
        services.AddKeyedSingleton(serviceKey, new RequestBatcherConfiguration<TRequest>(topicName, batcherName, options));
        RegisterHandlerDelegate<TRequest>(services, handlerLifetime, serviceKey);
        services.AddKeyedSingleton<RequestBatchCoordinator<TRequest>>(serviceKey);
    }
}
