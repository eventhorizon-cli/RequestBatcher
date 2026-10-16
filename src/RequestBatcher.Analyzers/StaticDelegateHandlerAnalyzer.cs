using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace RequestBatcher.Analyzers;

/// <summary>
/// Validates static targets and signatures supplied as delegate RequestBatcher handlers.
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
        "Delegate handler must be static",
        "The delegate handler must use a static lambda, anonymous method, local function, or method",
        "Usage",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Reject statically known non-static RequestBatcher delegate handler targets.",
        helpLinkUri: "https://github.com/eventhorizon-cli/RequestBatcher/blob/main/docs/delegate-handlers-design.md");

    private static readonly DiagnosticDescriptor _signatureRule = new(
        "RB0002",
        "Invalid delegate handler signature",
        "Invalid delegate handler signature: {0}",
        "Usage",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Require delegate handler signatures to match RequestBatcher batch and injection contracts.",
        helpLinkUri: "https://github.com/eventhorizon-cli/RequestBatcher/blob/main/docs/delegate-handlers-design.md");

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(_rule, _signatureRule);

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(startContext =>
        {
            var validator = new DelegateHandlerSignatureValidator(startContext.Compilation);
            startContext.RegisterOperationAction(
                operationContext => AnalyzeInvocation(operationContext, validator), OperationKind.Invocation);
        });
    }

    private static void AnalyzeInvocation(OperationAnalysisContext context, DelegateHandlerSignatureValidator validator)
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
        if (handlerArgument is null)
        {
            return;
        }

        var target = DelegateHandlerTargetResolver.Resolve(
            handlerArgument.Value, context.Compilation, context.CancellationToken);
        if (target.IsStatic == false)
        {
            context.ReportDiagnostic(Diagnostic.Create(_rule, handlerArgument.Syntax.GetLocation()));
            return;
        }

        var reason = validator.Validate(target.Invoke, target.Method, method.TypeArguments);
        if (reason is not null)
        {
            context.ReportDiagnostic(Diagnostic.Create(_signatureRule, handlerArgument.Syntax.GetLocation(), reason));
        }
    }

    private static bool IsRequestBatcherType(ITypeSymbol type) =>
        type is INamedTypeSymbol namedType &&
        namedType.Name == BatcherTypeName &&
        (namedType.Arity == 1 || namedType.Arity == 2) &&
        namedType.ContainingNamespace?.ToDisplayString() == ExtensionNamespaceName;

}
