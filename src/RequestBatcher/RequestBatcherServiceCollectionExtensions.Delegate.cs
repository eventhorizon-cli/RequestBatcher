using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RequestBatcher.Handlers.Delegate;

namespace RequestBatcher;

public static partial class RequestBatcherServiceCollectionExtensions
{
    /// <summary>
    /// Registers a default singleton batcher whose static delegate handler is supplied at execution time.
    /// </summary>
    /// <remarks>
    /// The handler and bound delegate are singleton per batcher. Service parameters retain their registered
    /// lifetimes and are resolved in an async scope per batch. Batches without service parameters create no scope.
    /// </remarks>
    /// <typeparam name="TRequest">The request type.</typeparam>
    /// <param name="services">The application-owned service collection.</param>
    /// <param name="configure">An optional callback that configures batching.</param>
    /// <returns>The service collection.</returns>
    public static IServiceCollection AddDelegateRequestBatcher<TRequest>(
        this IServiceCollection services,
        Action<RequestBatchOptions<TRequest>>? configure = null) =>
        AddDelegateRequestBatcherCore(services, null, configure);

    /// <summary>
    /// Registers a named singleton batcher whose static delegate handler is supplied at execution time.
    /// </summary>
    /// <remarks>
    /// The handler and bound delegate are singleton per batcher. Service parameters retain their registered
    /// lifetimes and are resolved in an async scope per batch. Batches without service parameters create no scope.
    /// </remarks>
    /// <typeparam name="TRequest">The request type.</typeparam>
    /// <param name="services">The application-owned service collection.</param>
    /// <param name="batcherName">The non-empty, case-sensitive batcher name used for keyed resolution.</param>
    /// <param name="configure">An optional callback that configures this batcher.</param>
    /// <returns>The service collection.</returns>
    public static IServiceCollection AddDelegateRequestBatcher<TRequest>(
        this IServiceCollection services,
        string batcherName,
        Action<RequestBatchOptions<TRequest>>? configure = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(batcherName);
        return AddDelegateRequestBatcherCore(services, batcherName, configure);
    }

    /// <summary>
    /// Registers a default singleton response batcher whose static delegate handler is supplied at execution time.
    /// </summary>
    /// <remarks>
    /// The handler and bound delegate are singleton per batcher. Service parameters retain their registered
    /// lifetimes and are resolved in an async scope per batch. Batches without service parameters create no scope.
    /// </remarks>
    /// <typeparam name="TRequest">The request type.</typeparam>
    /// <typeparam name="TResponse">The response type.</typeparam>
    /// <param name="services">The application-owned service collection.</param>
    /// <param name="configure">An optional callback that configures batching.</param>
    /// <returns>The service collection.</returns>
    public static IServiceCollection AddDelegateRequestBatcher<TRequest, TResponse>(
        this IServiceCollection services,
        Action<RequestBatchOptions<TRequest>>? configure = null) =>
        AddDelegateResponseBatcherCore<TRequest, TResponse>(services, null, configure);

    /// <summary>
    /// Registers a named singleton response batcher whose static delegate handler is supplied at execution time.
    /// </summary>
    /// <remarks>
    /// The handler and bound delegate are singleton per batcher. Service parameters retain their registered
    /// lifetimes and are resolved in an async scope per batch. Batches without service parameters create no scope.
    /// </remarks>
    /// <typeparam name="TRequest">The request type.</typeparam>
    /// <typeparam name="TResponse">The response type.</typeparam>
    /// <param name="services">The application-owned service collection.</param>
    /// <param name="batcherName">The non-empty, case-sensitive batcher name used for keyed resolution.</param>
    /// <param name="configure">An optional callback that configures this batcher.</param>
    /// <returns>The service collection.</returns>
    public static IServiceCollection AddDelegateRequestBatcher<TRequest, TResponse>(
        this IServiceCollection services,
        string batcherName,
        Action<RequestBatchOptions<TRequest>>? configure = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(batcherName);
        return AddDelegateResponseBatcherCore<TRequest, TResponse>(services, batcherName, configure);
    }

    private static IServiceCollection AddDelegateRequestBatcherCore<TRequest>(
        IServiceCollection services,
        string? batcherName,
        Action<RequestBatchOptions<TRequest>>? configure)
    {
        var options = RegisterDelegateOptions(services, batcherName, configure);
        var batcherKey = RegisterDelegateBatcher(services, options, batcherName);

        services.AddKeyedSingleton<DelegateRequestBatcher<TRequest>>(batcherKey);

        if (batcherName is null)
        {
            services.AddSingleton<IRequestBatcher<TRequest>>(
                provider => provider.GetRequiredKeyedService<DelegateRequestBatcher<TRequest>>(batcherKey));
        }
        else
        {
            services.AddKeyedSingleton<IRequestBatcher<TRequest>>(
                batcherName, (provider, _) => provider.GetRequiredKeyedService<DelegateRequestBatcher<TRequest>>(batcherKey));
        }

        return services;
    }

    private static IServiceCollection AddDelegateResponseBatcherCore<TRequest, TResponse>(
        IServiceCollection services,
        string? batcherName,
        Action<RequestBatchOptions<TRequest>>? configure)
    {
        var options = RegisterDelegateOptions(services, batcherName, configure);
        var entryOptions = options.Project<RequestBatchEntry<TRequest, TResponse>>(static entry => entry.Request);
        var batcherKey = RegisterDelegateBatcher(services, entryOptions, batcherName, ValidateResponses<TRequest, TResponse>);

        services.AddKeyedSingleton<ResponseRequestBatcher<TRequest, TResponse>>(batcherKey);
        services.AddKeyedSingleton<DelegateRequestBatcher<TRequest, TResponse>>(batcherKey);

        if (batcherName is null)
        {
            services.AddSingleton<IRequestBatcher<TRequest, TResponse>>(
                provider => provider.GetRequiredKeyedService<DelegateRequestBatcher<TRequest, TResponse>>(batcherKey));
        }
        else
        {
            services.AddKeyedSingleton<IRequestBatcher<TRequest, TResponse>>(
                batcherName, (provider, _) => provider.GetRequiredKeyedService<DelegateRequestBatcher<TRequest, TResponse>>(batcherKey));
        }

        return services;
    }

    private static RequestBatchOptions<TRequest> RegisterDelegateOptions<TRequest>(
        IServiceCollection services,
        string? batcherName,
        Action<RequestBatchOptions<TRequest>>? configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (batcherName is not null)
        {
            ValidateNamedRegistration<TRequest>(services, batcherName, ServiceLifetime.Singleton);
        }
        else if (services.Any(descriptor => !descriptor.IsKeyedService &&
                     (descriptor.ServiceType == typeof(RequestBatcherRegistration<TRequest>) ||
                      descriptor.ServiceType == typeof(IRequestBatcher<TRequest>) ||
                      descriptor.ServiceType == typeof(RequestBatchCoordinator<TRequest>))))
        {
            throw new InvalidOperationException($"A request batcher for '{typeof(TRequest)}' has already been registered.");
        }

        var options = CreateNamedOptions(configure);
        if (batcherName is null)
        {
            RegisterDefaultBatcher<TRequest>(services);
        }
        else
        {
            RegisterNamedBatcher<TRequest>(services, batcherName);
        }

        services.AddLogging();
        services.AddOptions<RequestBatchOptions<TRequest>>(batcherName ?? Options.DefaultName)
            .Configure(configured => configured.CopyFrom(options));
        return options;
    }

    private static object RegisterDelegateBatcher<TRequest>(
        IServiceCollection services,
        RequestBatchOptions<TRequest> options,
        string? batcherName,
        Action<IReadOnlyList<TRequest>>? validateResponses = null)
    {
        var batcherKey = new object();
        if (validateResponses is not null)
        {
            services.AddKeyedSingleton(batcherKey, validateResponses);
        }
        services.AddKeyedSingleton<DelegateRequestBatchHandler<TRequest>>(batcherKey);
        services.AddKeyedSingleton<IRequestBatchHandler<TRequest>>(
            batcherKey,
            (provider, key) => provider.GetRequiredKeyedService<DelegateRequestBatchHandler<TRequest>>(key));
        AddKeyedCoordinator(services, batcherKey, ServiceLifetime.Singleton, options, batcherName);
        return batcherKey;
    }

    private static void ValidateResponses<TRequest, TResponse>(
        IReadOnlyList<RequestBatchEntry<TRequest, TResponse>> entries)
    {
        foreach (var entry in entries)
        {
            _ = entry.GetResponse();
        }
    }
}
