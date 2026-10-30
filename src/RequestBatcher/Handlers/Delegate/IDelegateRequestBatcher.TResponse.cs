using System.Diagnostics.CodeAnalysis;

namespace RequestBatcher.Handlers.Delegate;

internal interface IDelegateRequestBatcher<TRequest, TResponse>
{
    [RequiresDynamicCode("Delegate handlers compile a typed invoker on first use.")]
    [RequiresUnreferencedCode("Delegate handlers inspect the delegate signature and service parameter attributes.")]
    Task<TResponse> SubmitAsync(TRequest request, System.Delegate handler, CancellationToken cancellationToken);

    [RequiresDynamicCode("Delegate handlers compile a typed invoker on first use.")]
    [RequiresUnreferencedCode("Delegate handlers inspect the delegate signature and service parameter attributes.")]
    Task<IReadOnlyList<TResponse>> SubmitAsync(
        IEnumerable<TRequest> requests,
        System.Delegate handler,
        CancellationToken cancellationToken);
}
