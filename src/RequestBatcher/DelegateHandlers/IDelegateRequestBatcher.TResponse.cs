using System.Diagnostics.CodeAnalysis;

namespace RequestBatcher.DelegateHandlers;

internal interface IDelegateRequestBatcher<TRequest, TResponse>
{
    [RequiresDynamicCode("Delegate handlers compile a typed invoker on first use.")]
    [RequiresUnreferencedCode("Delegate handlers inspect the delegate signature and service parameter attributes.")]
    Task<TResponse> SubmitAsync(TRequest request, Delegate handler, CancellationToken cancellationToken);

    [RequiresDynamicCode("Delegate handlers compile a typed invoker on first use.")]
    [RequiresUnreferencedCode("Delegate handlers inspect the delegate signature and service parameter attributes.")]
    Task<IReadOnlyList<TResponse>> SubmitAsync(
        IEnumerable<TRequest> requests,
        Delegate handler,
        CancellationToken cancellationToken);
}
