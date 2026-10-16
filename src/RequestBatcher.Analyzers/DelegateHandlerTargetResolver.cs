using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace RequestBatcher.Analyzers;

internal static class DelegateHandlerTargetResolver
{
    internal static Target Resolve(IOperation operation, Compilation compilation, CancellationToken cancellationToken) =>
        Resolve(operation, compilation, cancellationToken, new HashSet<ISymbol>(SymbolEqualityComparer.Default));

    private static Target Resolve(
        IOperation operation,
        Compilation compilation,
        CancellationToken cancellationToken,
        HashSet<ISymbol> visited)
    {
        var invoke = (operation.Type as INamedTypeSymbol)?.DelegateInvokeMethod;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (operation)
            {
                case IConversionOperation conversion:
                    operation = conversion.Operand;
                    invoke = (operation.Type as INamedTypeSymbol)?.DelegateInvokeMethod ?? invoke;
                    continue;
                case IParenthesizedOperation parenthesized:
                    operation = parenthesized.Operand;
                    continue;
                case IDelegateCreationOperation creation:
                    invoke = (creation.Type as INamedTypeSymbol)?.DelegateInvokeMethod ?? invoke;
                    operation = creation.Target;
                    continue;
                case IAnonymousFunctionOperation lambda:
                    var isStatic = lambda.Syntax switch
                    {
                        LambdaExpressionSyntax expression => expression.Modifiers.Any(SyntaxKind.StaticKeyword),
                        AnonymousMethodExpressionSyntax expression => expression.Modifiers.Any(SyntaxKind.StaticKeyword),
                        _ => false
                    };
                    return new Target(invoke, lambda.Symbol, isStatic);
                case IMethodReferenceOperation method:
                    if (method.Method.MethodKind == MethodKind.DelegateInvoke && method.Instance is not null)
                    {
                        var target = Resolve(method.Instance, compilation, cancellationToken, visited);
                        return new Target(invoke ?? target.Invoke, target.Method, target.IsStatic);
                    }

                    return new Target(invoke, method.Method,
                        method.Method.IsStatic && method.Method.ReducedFrom is null && method.Instance is null);
                case ILocalReferenceOperation local when visited.Count < 32 && visited.Add(local.Local):
                    var initializer = GetUnchangedInitializer(local.Local, compilation, cancellationToken);
                    if (initializer is not null)
                    {
                        var target = Resolve(initializer, compilation, cancellationToken, visited);
                        return new Target(target.Invoke ?? invoke, target.Method, target.IsStatic);
                    }

                    break;
            }

            return new Target(invoke, null, null);
        }
    }

    private static IOperation? GetUnchangedInitializer(
        ILocalSymbol local,
        Compilation compilation,
        CancellationToken cancellationToken)
    {
        if (local.DeclaringSyntaxReferences.Length != 1 ||
            local.DeclaringSyntaxReferences[0].GetSyntax(cancellationToken) is not VariableDeclaratorSyntax
            { Initializer.Value: { } value } declaration)
        {
            return null;
        }

        var model = compilation.GetSemanticModel(declaration.SyntaxTree);
        // Search the whole owning function, including nested functions that can mutate the local.
        var owner = declaration.Ancestors().FirstOrDefault(static syntax =>
            syntax is BaseMethodDeclarationSyntax or LocalFunctionStatementSyntax or
                AnonymousFunctionExpressionSyntax or GlobalStatementSyntax);
        if (owner is GlobalStatementSyntax)
        {
            owner = declaration.SyntaxTree.GetRoot(cancellationToken);
        }

        if (owner is null)
        {
            return null;
        }

        foreach (var identifier in owner.DescendantNodes().OfType<IdentifierNameSyntax>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (identifier.Identifier.ValueText != local.Name ||
                !SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(identifier, cancellationToken).Symbol, local))
            {
                continue;
            }

            foreach (var ancestor in identifier.Ancestors())
            {
                if (ancestor is AssignmentExpressionSyntax assignment && assignment.Left.Span.Contains(identifier.Span) ||
                    ancestor is RefExpressionSyntax ||
                    ancestor is ArgumentSyntax argument && argument.RefKindKeyword.Kind() is SyntaxKind.RefKeyword or SyntaxKind.OutKeyword ||
                    ancestor is PrefixUnaryExpressionSyntax prefix &&
                    prefix.Kind() is SyntaxKind.PreIncrementExpression or SyntaxKind.PreDecrementExpression ||
                    ancestor is PostfixUnaryExpressionSyntax postfix &&
                    postfix.Kind() is SyntaxKind.PostIncrementExpression or SyntaxKind.PostDecrementExpression)
                {
                    return null;
                }

                if (ancestor is StatementSyntax)
                {
                    break;
                }
            }
        }

        return model.GetOperation(value, cancellationToken);
    }

    internal sealed class Target(IMethodSymbol? invoke, IMethodSymbol? method, bool? isStatic)
    {
        internal IMethodSymbol? Invoke { get; } = invoke;
        internal IMethodSymbol? Method { get; } = method;
        internal bool? IsStatic { get; } = isStatic;
    }
}
