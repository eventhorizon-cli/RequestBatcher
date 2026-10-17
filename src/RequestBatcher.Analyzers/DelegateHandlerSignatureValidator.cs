using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace RequestBatcher.Analyzers;

internal sealed class DelegateHandlerSignatureValidator(Compilation compilation)
{
    private readonly INamedTypeSymbol? _list = compilation.GetTypeByMetadataName("System.Collections.Generic.IReadOnlyList`1");
    private readonly INamedTypeSymbol? _entry = compilation.GetTypeByMetadataName("RequestBatcher.RequestBatchEntry`2");
    private readonly INamedTypeSymbol? _task = compilation.GetTypeByMetadataName("System.Threading.Tasks.Task");
    private readonly INamedTypeSymbol? _valueTask = compilation.GetTypeByMetadataName("System.Threading.Tasks.ValueTask");
    private readonly INamedTypeSymbol? _token = compilation.GetTypeByMetadataName("System.Threading.CancellationToken");
    private readonly INamedTypeSymbol? _keyed = compilation.GetTypeByMetadataName("Microsoft.Extensions.DependencyInjection.FromKeyedServicesAttribute");
    private readonly INamedTypeSymbol? _asyncStateMachine = compilation.GetTypeByMetadataName("System.Runtime.CompilerServices.AsyncStateMachineAttribute");

    internal string? Validate(IMethodSymbol? invoke, IMethodSymbol? source, ImmutableArray<ITypeSymbol> typeArguments)
    {
        if (invoke is null ||
            _list is null || typeArguments.Length is < 1 or > 2 ||
            typeArguments.Any(static type => type.TypeKind == TypeKind.Error))
        {
            return null;
        }

        var element = typeArguments[0];
        if (typeArguments.Length == 2)
        {
            if (_entry is null)
            {
                return null;
            }

            element = _entry.Construct(typeArguments[0], typeArguments[1]);
        }

        var batch = _list.Construct(element);
        if (invoke.Parameters.Length == 0 ||
            (!IsSameType(invoke.Parameters[0].Type, batch) &&
             (source is not null || !MayVary(invoke.OriginalDefinition.Parameters[0].Type, invoke.ContainingType))))
        {
            return $"The first parameter must be '{batch.ToDisplayString()}'.";
        }

        if (invoke.ReturnsByRef || invoke.ReturnsByRefReadonly ||
            (invoke.ReturnType.SpecialType != SpecialType.System_Void &&
             !IsSameType(invoke.ReturnType, _task) && !IsSameType(invoke.ReturnType, _valueTask) &&
             (source is not null || !MayVary(invoke.OriginalDefinition.ReturnType, invoke.ContainingType))))
        {
            return "The return type must be void, non-generic Task, or non-generic ValueTask, without a by-reference return.";
        }

        if (invoke.ReturnsVoid && source is not null &&
            (source.IsAsync || source.GetAttributes().Any(
                attribute => SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, _asyncStateMachine))))
        {
            return "Async void handlers are not supported.";
        }

        if (source is not null && source.Parameters.Length != invoke.Parameters.Length)
        {
            return null;
        }

        var hasToken = false;
        for (var index = 0; index < invoke.Parameters.Length; index++)
        {
            var parameter = invoke.Parameters[index];
            var sourceParameter = source?.Parameters[index];
            var keyed = sourceParameter?.GetAttributes().FirstOrDefault(
                attribute => SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, _keyed));
            if (keyed is not null && keyed.ConstructorArguments.Length == 0)
            {
                return "FromKeyedServices must specify an explicit key for a delegate handler.";
            }

            if (parameter.IsOptional || parameter.HasExplicitDefaultValue ||
                sourceParameter?.IsOptional == true || sourceParameter?.HasExplicitDefaultValue == true)
            {
                return "Optional parameters and default parameter values are not supported.";
            }

            if (parameter.RefKind != RefKind.None || sourceParameter is { RefKind: not RefKind.None })
            {
                return "By-reference parameters are not supported.";
            }

            if (parameter.Type.TypeKind is TypeKind.Pointer or TypeKind.FunctionPointer)
            {
                return "Pointer and function-pointer parameters are not supported.";
            }

            if (parameter.Type is INamedTypeSymbol { IsRefLikeType: true })
            {
                return "Ref-like parameters are not supported.";
            }

            if (index == 0)
            {
                if (keyed is not null)
                {
                    return "The batch parameter cannot use FromKeyedServices.";
                }

                continue;
            }

            if (IsSameType(parameter.Type, _token))
            {
                if (hasToken || keyed is not null)
                {
                    return "At most one unkeyed CancellationToken parameter is supported.";
                }

                hasToken = true;
            }
        }

        return null;
    }

    private bool IsSameType(ITypeSymbol actual, ITypeSymbol? expected) =>
        expected is not null && compilation is CSharpCompilation csharp &&
        csharp.ClassifyConversion(actual, expected).IsIdentity;

    private static bool MayVary(ITypeSymbol template, INamedTypeSymbol delegateType) => template switch
    {
        ITypeParameterSymbol parameter when SymbolEqualityComparer.Default.Equals(
            parameter.ContainingSymbol, delegateType.OriginalDefinition) =>
            delegateType.TypeParameters[parameter.Ordinal].Variance != VarianceKind.None &&
            !delegateType.TypeArguments[parameter.Ordinal].IsValueType,
        INamedTypeSymbol named => named.TypeArguments.Any(argument => MayVary(argument, delegateType)),
        IArrayTypeSymbol array => MayVary(array.ElementType, delegateType),
        _ => false
    };
}
