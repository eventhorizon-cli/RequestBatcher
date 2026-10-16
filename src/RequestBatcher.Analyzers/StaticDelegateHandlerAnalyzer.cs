using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace RequestBatcher.Analyzers;

/// <summary>
/// Requires delegate RequestBatcher handlers to be supplied as static lambda expressions.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class StaticDelegateHandlerAnalyzer : DiagnosticAnalyzer
{
    private const string ExtensionTypeName = "RequestBatcherDelegateExtensions";
    private const string ExtensionNamespaceName = "RequestBatcher";
    private const string BatcherTypeName = "IRequestBatcher";
    private const string ExecuteMethodName = "ExecuteAsync";
    private const string HandlerParameterName = "handler";

    private static readonly DiagnosticDescriptor _rule = new(
        "RB0001",
        "Delegate handler must be a static lambda",
        "The delegate handler argument must be supplied as a static lambda expression",
        "Usage",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Require RequestBatcher delegate handlers to be directly supplied static lambdas.",
        helpLinkUri: "https://github.com/eventhorizon-cli/RequestBatcher/blob/main/docs/delegate-handlers-design.md");

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(_rule);

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterOperationAction(AnalyzeInvocation, OperationKind.Invocation);
    }

    private static void AnalyzeInvocation(OperationAnalysisContext context)
    {
        var invocation = (IInvocationOperation)context.Operation;
        var method = invocation.TargetMethod;
        var containingType = method.ContainingType;
        var extensionMethod = method.ReducedFrom ?? method;
        if (method.Name != ExecuteMethodName ||
            containingType is null ||
            containingType.Name != ExtensionTypeName ||
            containingType.ContainingNamespace?.ToDisplayString() != ExtensionNamespaceName ||
            !extensionMethod.IsExtensionMethod ||
            extensionMethod.Parameters.Length == 0 ||
            !IsRequestBatcherType(extensionMethod.Parameters[0].Type))
        {
            return;
        }

        var handlerParameter = method.Parameters.FirstOrDefault(
            static parameter => parameter.Name == HandlerParameterName);
        if (handlerParameter is null || handlerParameter.Type.SpecialType != SpecialType.System_Delegate)
        {
            return;
        }

        var handlerArgument = invocation.Arguments.FirstOrDefault(
            argument => SymbolEqualityComparer.Default.Equals(argument.Parameter, handlerParameter));
        if (handlerArgument is null || IsStaticLambda(handlerArgument.Value))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(_rule, handlerArgument.Syntax.GetLocation()));
    }

    private static bool IsRequestBatcherType(ITypeSymbol type) =>
        type is INamedTypeSymbol namedType &&
        namedType.Name == BatcherTypeName &&
        (namedType.Arity == 1 || namedType.Arity == 2) &&
        namedType.ContainingNamespace?.ToDisplayString() == ExtensionNamespaceName;

    private static bool IsStaticLambda(IOperation operation)
    {
        var syntax = operation.Syntax;
        while (syntax is CastExpressionSyntax or ParenthesizedExpressionSyntax)
        {
            syntax = syntax switch
            {
                CastExpressionSyntax cast => cast.Expression,
                ParenthesizedExpressionSyntax parenthesized => parenthesized.Expression,
                _ => syntax
            };
        }

        return syntax is LambdaExpressionSyntax lambda &&
            lambda.Modifiers.Any(static modifier => modifier.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.StaticKeyword));
    }
}
