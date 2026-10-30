using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;

namespace RequestBatcher.Handlers.Delegate;

internal sealed class DelegateRequestBatchHandler<TRequest>(
    IServiceScopeFactory scopeFactory,
    [FromKeyedServices] Action<IReadOnlyList<TRequest>>? validateResponses = null)
    : IRequestBatchHandler<TRequest>
{
    private readonly object _bindingLock = new();
    private Binding? _binding;

    [RequiresDynamicCode("Delegate handlers compile a typed invoker on first use.")]
    [RequiresUnreferencedCode("Delegate handlers inspect the delegate signature and service parameter attributes.")]
    public void Bind(System.Delegate handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        var binding = Volatile.Read(ref _binding);
        if (binding is not null)
        {
            EnsureSameHandler(binding, handler);
            return;
        }

        lock (_bindingLock)
        {
            binding = _binding;
            if (binding is null)
            {
                binding = CreateBinding(handler);
                Volatile.Write(ref _binding, binding);
            }
            else
            {
                EnsureSameHandler(binding, handler);
            }
        }
    }

    public ValueTask HandleAsync(
        IReadOnlyList<TRequest> requests,
        CancellationToken cancellationToken = default)
    {
        var binding = Volatile.Read(ref _binding) ??
            throw new InvalidOperationException("The delegate handler has not been bound.");

        if (binding.RequiresServices)
        {
            // Parameter metadata identifies services, not their registered lifetimes. IServiceProvider
            // has no standard lifetime query, so even singleton-only parameters use a batch scope.
            return HandleInScopeAsync(binding, requests, cancellationToken);
        }

        // Return the actual operation, which the consumer still awaits before completing requests.
        // Only response handlers need post-invocation work on the no-service path.
        return validateResponses is null
            ? binding.Invoke(requests, null!, cancellationToken)
            : HandleWithoutServicesAsync(binding, requests, cancellationToken);
    }

    private async ValueTask HandleWithoutServicesAsync(
        Binding binding,
        IReadOnlyList<TRequest> requests,
        CancellationToken cancellationToken)
    {
        // Response delegates must finish before their entries can be validated.
        await binding.Invoke(requests, null!, cancellationToken).ConfigureAwait(false);
        validateResponses?.Invoke(requests);
    }

    private async ValueTask HandleInScopeAsync(
        Binding binding,
        IReadOnlyList<TRequest> requests,
        CancellationToken cancellationToken)
    {
        // Keep injected services alive through invocation and validation; completion also waits for async disposal.
        // This scope belongs to the batch, not a submitting caller. Singletons remain shared;
        // container-owned singletons are disposed with the root, not with this batch scope.
        await using var scope = scopeFactory.CreateAsyncScope();
        await binding.Invoke(requests, scope.ServiceProvider, cancellationToken).ConfigureAwait(false);
        validateResponses?.Invoke(requests);
    }

    private static void EnsureSameHandler(Binding binding, System.Delegate handler)
    {
        if (!ReferenceEquals(binding.Handler, handler) && !binding.Handler.Equals(handler))
        {
            throw new InvalidOperationException(
                "A different delegate handler has already been bound to this request batcher. " +
                "Use a separate batcher name for a different handler.");
        }
    }

    [RequiresDynamicCode("Delegate handlers compile a typed invoker on first use.")]
    [RequiresUnreferencedCode("Delegate handlers inspect the delegate signature and service parameter attributes.")]
    private static Binding CreateBinding(System.Delegate handler)
    {
        var sourceHandler = handler;
        while (true)
        {
            if (sourceHandler.GetInvocationList().Length != 1 || sourceHandler.Method.ContainsGenericParameters)
            {
                throw InvalidHandler("Only a single closed delegate is supported.");
            }

            if (sourceHandler.Method.Name != "Invoke" ||
                sourceHandler.Method.DeclaringType is not { } declaringType ||
                !typeof(System.Delegate).IsAssignableFrom(declaringType) || sourceHandler.Target is not System.Delegate target)
            {
                break;
            }

            // A delegate copy can wrap Invoke; binding metadata belongs to the original method.
            sourceHandler = target;
        }

        var invokeMethod = handler.GetType().GetMethod("Invoke")!;
        var parameters = invokeMethod.GetParameters();
        var sourceParameters = sourceHandler.Method.GetParameters();
        if (parameters.Length == 0 || parameters[0].ParameterType != typeof(IReadOnlyList<TRequest>) ||
            parameters.Length != sourceParameters.Length)
        {
            throw InvalidHandler($"The first parameter must be {typeof(IReadOnlyList<TRequest>)}.");
        }

        var returnType = invokeMethod.ReturnType;
        if (returnType != typeof(void) && returnType != typeof(Task) && returnType != typeof(ValueTask))
        {
            throw InvalidHandler("The return type must be void, Task, or ValueTask.");
        }

        if (returnType == typeof(void) && sourceHandler.Method.IsDefined(typeof(AsyncStateMachineAttribute), false))
        {
            throw InvalidHandler("Async void handlers are not supported.");
        }

        var requests = Expression.Parameter(typeof(IReadOnlyList<TRequest>), "requests");
        var provider = Expression.Parameter(typeof(IServiceProvider), "provider");
        var cancellationToken = Expression.Parameter(typeof(CancellationToken), "cancellationToken");
        var arguments = new Expression[parameters.Length];
        arguments[0] = requests;
        var requiresServices = false;
        var hasCancellationToken = false;

        for (var index = 0; index < parameters.Length; index++)
        {
            var parameterType = parameters[index].ParameterType;
            var source = sourceParameters[index];
            var keyedService = source.GetCustomAttribute<FromKeyedServicesAttribute>();
            if (keyedService?.LookupMode == ServiceKeyLookupMode.InheritKey)
            {
                throw InvalidHandler("FromKeyedServices must specify an explicit key for a delegate handler.");
            }

            if (parameterType.IsByRef || parameterType.IsPointer || parameterType.IsByRefLike ||
                parameterType.ContainsGenericParameters || parameters[index].IsOptional || source.IsOptional)
            {
                throw InvalidHandler("Optional, by-reference, pointer, and open generic parameters are not supported.");
            }

            if (index == 0)
            {
                if (keyedService is not null)
                {
                    throw InvalidHandler("The batch parameter cannot select a keyed service.");
                }

                continue;
            }

            if (parameterType == typeof(CancellationToken))
            {
                if (hasCancellationToken || keyedService is not null)
                {
                    throw InvalidHandler("At most one unkeyed CancellationToken parameter is supported.");
                }

                hasCancellationToken = true;
                arguments[index] = cancellationToken;
                continue;
            }

            requiresServices = true;
            var service = keyedService is null
                ? Expression.Call(
                    typeof(ServiceProviderServiceExtensions),
                    nameof(ServiceProviderServiceExtensions.GetRequiredService),
                    Type.EmptyTypes,
                    provider,
                    Expression.Constant(parameterType))
                : Expression.Call(
                    typeof(ServiceProviderKeyedServiceExtensions),
                    nameof(ServiceProviderKeyedServiceExtensions.GetRequiredKeyedService),
                    Type.EmptyTypes,
                    provider,
                    Expression.Constant(parameterType),
                    Expression.Constant(keyedService.Key, typeof(object)));
            arguments[index] = Expression.Convert(service, parameterType);
        }

        var call = Expression.Invoke(Expression.Constant(handler, handler.GetType()), arguments);
        Expression body = returnType == typeof(void)
            ? Expression.Block(call, Expression.Default(typeof(ValueTask)))
            : returnType == typeof(Task)
                ? Expression.New(typeof(ValueTask).GetConstructor([typeof(Task)])!, call)
                : call;
        // Compile one uniform invoker for service injection, supported return types and custom delegate types.
        // Service-free signatures could use direct binding, but that would add separate adapters for each
        // return/token combination and delegate form. Keep the shared binding path to limit maintenance cost.
        // Compilation happens only on initial binding; later batches reuse the invoker without reflection
        // or expression compilation. The request-only ValueTask fast path skips an await wrapper, not binding.
        var invoker = Expression.Lambda<
            Func<IReadOnlyList<TRequest>, IServiceProvider, CancellationToken, ValueTask>>(
            body, requests, provider, cancellationToken).Compile();

        return new Binding(handler, invoker, requiresServices);
    }

    private static ArgumentException InvalidHandler(string message) => new(message, "handler");

    private sealed record Binding(
        System.Delegate Handler,
        Func<IReadOnlyList<TRequest>, IServiceProvider, CancellationToken, ValueTask> Invoke,
        bool RequiresServices);
}
