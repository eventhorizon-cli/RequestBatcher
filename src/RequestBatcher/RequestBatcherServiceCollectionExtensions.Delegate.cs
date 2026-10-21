using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RequestBatcher.DelegateHandlers;

namespace RequestBatcher;

public static partial class RequestBatcherServiceCollectionExtensions
{
    /// <summary>
    /// Registers a default singleton batcher whose static delegate handler is supplied at execution time.
    /// </summary>
    /// <remarks>
    /// The handler and bound delegate are singleton per pipeline. Service parameters retain their registered
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
    /// The handler and bound delegate are singleton per pipeline. Service parameters retain their registered
    /// lifetimes and are resolved in an async scope per batch. Batches without service parameters create no scope.
    /// </remarks>
    /// <typeparam name="TRequest">The request type.</typeparam>
    /// <param name="services">The application-owned service collection.</param>
    /// <param name="batcherName">The non-empty, case-sensitive batcher name used for keyed resolution.</param>
    /// <param name="configure">An optional callback that configures this pipeline.</param>
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
    /// The handler and bound delegate are singleton per pipeline. Service parameters retain their registered
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
    /// The handler and bound delegate are singleton per pipeline. Service parameters retain their registered
    /// lifetimes and are resolved in an async scope per batch. Batches without service parameters create no scope.
    /// </remarks>
    /// <typeparam name="TRequest">The request type.</typeparam>
    /// <typeparam name="TResponse">The response type.</typeparam>
    /// <param name="services">The application-owned service collection.</param>
    /// <param name="batcherName">The non-empty, case-sensitive batcher name used for keyed resolution.</param>
    /// <param name="configure">An optional callback that configures this pipeline.</param>
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
        var pipelineKey = RegisterDelegatePipeline(services, options, batcherName);

        DelegateRequestBatcher<TRequest> CreateBatcher(IServiceProvider provider) => new(
            provider.GetRequiredKeyedService<RequestBatchCoordinator<TRequest>>(pipelineKey),
            provider.GetRequiredKeyedService<DelegateRequestBatchHandler<TRequest>>(pipelineKey));

        if (batcherName is null)
        {
            services.AddSingleton<IRequestBatcher<TRequest>>(CreateBatcher);
        }
        else
        {
            services.AddKeyedSingleton<IRequestBatcher<TRequest>>(batcherName, (provider, _) => CreateBatcher(provider));
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
        var pipelineKey = RegisterDelegatePipeline(services, entryOptions, batcherName, ValidateResponses<TRequest, TResponse>);

        DelegateRequestBatcher<TRequest, TResponse> CreateBatcher(IServiceProvider provider) => new(
            new ResponseRequestBatcher<TRequest, TResponse>(
                provider.GetRequiredKeyedService<RequestBatchCoordinator<RequestBatchEntry<TRequest, TResponse>>>(
                    pipelineKey)),
            provider.GetRequiredKeyedService<DelegateRequestBatchHandler<RequestBatchEntry<TRequest, TResponse>>>(
                pipelineKey));

        if (batcherName is null)
        {
            services.AddSingleton<IRequestBatcher<TRequest, TResponse>>(CreateBatcher);
        }
        else
        {
            services.AddKeyedSingleton<IRequestBatcher<TRequest, TResponse>>(
                batcherName, (provider, _) => CreateBatcher(provider));
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
            RegisterBatcherPipeline<TRequest>(services);
        }
        else
        {
            RegisterNamedPipeline<TRequest>(services, batcherName);
        }

        services.AddLogging();
        services.AddOptions<RequestBatchOptions<TRequest>>(batcherName ?? Options.DefaultName)
            .Configure(configured => configured.CopyFrom(options));
        return options;
    }

    private static object RegisterDelegatePipeline<TRequest>(
        IServiceCollection services,
        RequestBatchOptions<TRequest> options,
        string? batcherName,
        Action<IReadOnlyList<TRequest>>? validateResponses = null)
    {
        var pipelineKey = new object();
        services.AddKeyedSingleton<DelegateRequestBatchHandler<TRequest>>(
            pipelineKey,
            (provider, _) => new DelegateRequestBatchHandler<TRequest>(
                provider.GetRequiredService<IServiceScopeFactory>(), validateResponses));
        services.AddKeyedSingleton<IRequestBatchHandler<TRequest>>(
            pipelineKey,
            (provider, key) => provider.GetRequiredKeyedService<DelegateRequestBatchHandler<TRequest>>(key));
        AddKeyedCoordinator(services, pipelineKey, ServiceLifetime.Singleton, options, batcherName);
        return pipelineKey;
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
