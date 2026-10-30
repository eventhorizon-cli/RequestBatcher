using Microsoft.Extensions.DependencyInjection;

namespace RequestBatcher.Handlers;

internal sealed class ScopedRequestBatchHandler<TRequest>(
    IServiceScopeFactory scopeFactory,
    [ServiceKey] object? serviceKey = null)
{
    public async ValueTask HandleAsync(IReadOnlyList<TRequest> requests, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var handler = serviceKey is null
            ? scope.ServiceProvider.GetRequiredService<IRequestBatchHandler<TRequest>>()
            : scope.ServiceProvider.GetRequiredKeyedService<IRequestBatchHandler<TRequest>>(serviceKey);
        await handler.HandleAsync(requests, cancellationToken).ConfigureAwait(false);
    }
}
