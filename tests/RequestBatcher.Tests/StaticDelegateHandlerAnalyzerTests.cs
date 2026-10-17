using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using RequestBatcher.Analyzers;

namespace RequestBatcher.Tests;

public sealed class StaticDelegateHandlerAnalyzerTests
{
    [Fact]
    public async Task AnalyzeInvocation_StaticLambdasAcrossOverloads_AreAccepted()
    {
        const string source = ApiSource + """

        public static class Caller
        {
            public static void Run(
                RequestBatcher.IRequestBatcher<int> batcher,
                RequestBatcher.IRequestBatcher<int, string> responseBatcher)
            {
                batcher.ExecuteAsync(1, static (IReadOnlyList<int> values) => { });
                RequestBatcher.RequestBatcherDelegateExtensions.ExecuteAsync(
                    batcher: batcher,
                    requests: new[] { 1 },
                    handler: (((Delegate)((static (IReadOnlyList<int> values) => { })))),
                    cancellationToken: default);
                RequestBatcher.RequestBatcherDelegateExtensions.ExecuteAsync(
                    batcher,
                    request: 1,
                    handler: (static (IReadOnlyList<int> values) => { }));
                responseBatcher.ExecuteAsync(
                    1,
                    (static (IReadOnlyList<RequestBatcher.RequestBatchEntry<int, string>> entries) => { }));
                responseBatcher.ExecuteAsync(
                    new[] { 1 },
                    handler: (Delegate)(static (IReadOnlyList<RequestBatcher.RequestBatchEntry<int, string>> entries) => { }));
                batcher.ExecuteAsync(1);
            }
        }
        """;

        var diagnostics = await AnalyzeAsync(source);

        Assert.Empty(diagnostics);
    }

    [Theory]
    [InlineData("Handle")]
    [InlineData("GenericHandle<int>")]
    [InlineData("Local")]
    [InlineData("GenericLocal<int>")]
    [InlineData("new Action<IReadOnlyList<int>>(Handle)")]
    [InlineData("(Action<IReadOnlyList<int>>)Handle")]
    [InlineData("(Delegate)((Action<IReadOnlyList<int>>)Handle)")]
    [InlineData("static delegate(IReadOnlyList<int> values) { }")]
    [InlineData("variable")]
    [InlineData("alias")]
    [InlineData("StoredHandler")]
    [InlineData("new Action<IReadOnlyList<int>>(typedVariable)")]
    [InlineData("new Action<IReadOnlyList<int>>(StoredHandler)")]
    public async Task AnalyzeInvocation_KnownStaticHandlerForms_AreAccepted(string handler)
    {
        var source = ApiSource + $$"""

        public static class Caller
        {
            private static readonly Action<IReadOnlyList<int>> StoredHandler = Handle;

            private static void Handle(IReadOnlyList<int> values) { }

            private static void GenericHandle<T>(IReadOnlyList<T> values) { }

            public static void Run(IRequestBatcher<int> batcher)
            {
                static void Local(IReadOnlyList<int> values) { }
                static void GenericLocal<T>(IReadOnlyList<T> values) { }
                Action<IReadOnlyList<int>> typedVariable = Handle;
                Delegate variable = typedVariable;
                var alias = variable;
                batcher.ExecuteAsync(1, {{handler}});
            }
        }
        """;

        var diagnostics = await AnalyzeAsync(source);

        Assert.Empty(diagnostics);
    }

    [Theory]
    [InlineData("(IReadOnlyList<int> values) => { }")]
    [InlineData("(int value) => _ = captured")]
    [InlineData("delegate(IReadOnlyList<int> values) { }")]
    [InlineData("instance.Handle")]
    [InlineData("instance.ExtensionHandle")]
    [InlineData("Local")]
    [InlineData("variable")]
    [InlineData("new Action<IReadOnlyList<int>>(instance.Handle)")]
    [InlineData("new Action<IReadOnlyList<int>>(variable)")]
    public async Task AnalyzeInvocation_KnownNonStaticHandlerForms_ReportStaticError(string handler)
    {
        var source = ApiSource + $$"""

        public sealed class Receiver
        {
            public void Handle(IReadOnlyList<int> values) { }
        }

        public static class ReceiverExtensions
        {
            public static void ExtensionHandle(this Receiver receiver, IReadOnlyList<int> values) { }
        }

        public static class Caller
        {
            public static void Run(IRequestBatcher<int> batcher)
            {
                var captured = 1;
                var instance = new Receiver();
                void Local(IReadOnlyList<int> values) { }
                Action<IReadOnlyList<int>> variable = instance.Handle;
                batcher.ExecuteAsync(1, {{handler}});
            }
        }
        """;

        var diagnostics = await AnalyzeAsync(source);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("RB0001", diagnostic.Id);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
    }

    [Theory]
    [InlineData("Handle")]
    [InlineData("Local")]
    [InlineData("variable")]
    public async Task AnalyzeInvocation_StaticMethodsWithServiceMetadata_AreAccepted(string handler)
    {
        var source = ApiSource + $$"""

        public static class Caller
        {
            private static Task Handle(
                IReadOnlyList<int> values,
                [FromKeyedServices("store")] IDisposable keyed,
                [Microsoft.AspNetCore.Mvc.FromServices] IServiceProvider provider,
                CancellationToken token) => Task.CompletedTask;

            public static void Run(IRequestBatcher<int> batcher)
            {
                static Task Local(
                    IReadOnlyList<int> values,
                    [FromKeyedServices(null)] IDisposable keyed,
                    IServiceProvider provider,
                    CancellationToken token) => Task.CompletedTask;
                Delegate variable = (Func<IReadOnlyList<int>, IDisposable, IServiceProvider, CancellationToken, Task>)Handle;
                batcher.ExecuteAsync(1, {{handler}});
            }
        }
        """;

        var diagnostics = await AnalyzeAsync(source);

        Assert.Empty(diagnostics);
    }

    [Theory]
    [InlineData("public static void Handle(int value) { }", "Handle")]
    [InlineData("public static void Handle(IReadOnlyList<int> values) { }", "(Action<List<int>>)Handle")]
    [InlineData("public static int Handle(IReadOnlyList<int> values) => 1;", "Handle")]
    [InlineData("public static async void Handle(IReadOnlyList<int> values) { await Task.Yield(); }", "Handle")]
    [InlineData("public static void Handle(IReadOnlyList<int> values, CancellationToken token = default) { }", "(Action<IReadOnlyList<int>, CancellationToken>)Handle")]
    [InlineData("public static void Handle(IReadOnlyList<int> values, [FromKeyedServices] IDisposable service) { }", "Handle")]
    [InlineData("public static void Handle([FromKeyedServices(\"store\")] IReadOnlyList<int> values) { }", "Handle")]
    [InlineData("public static void Handle(IReadOnlyList<int> values, [FromKeyedServices(\"store\")] CancellationToken token) { }", "Handle")]
    public async Task AnalyzeInvocation_InvalidStaticMethodSignatures_ReportSignatureError(
        string declaration,
        string handler)
    {
        var source = ApiSource + $$"""

        public static class Caller
        {
            {{declaration}}

            public static void Run(IRequestBatcher<int> batcher)
            {
                batcher.ExecuteAsync(1, {{handler}});
            }
        }
        """;

        var diagnostics = await AnalyzeAsync(source);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("RB0002", diagnostic.Id);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
    }

    [Fact]
    public async Task AnalyzeInvocation_VariantMethodSignaturesWithExactDelegateTypes_AreAccepted()
    {
        const string source = ApiSource + """

        public static class Caller
        {
            private static void Handle(object values) { }

            private static Task<int> HandleWithResult(IReadOnlyList<int> values) => Task.FromResult(1);

            public static void Run(IRequestBatcher<int> batcher)
            {
                batcher.ExecuteAsync(1, new Action<IReadOnlyList<int>>(Handle));
                batcher.ExecuteAsync(1, new Func<IReadOnlyList<int>, Task>(HandleWithResult));
            }
        }
        """;

        var diagnostics = await AnalyzeAsync(source);

        Assert.Empty(diagnostics);
    }

    [Theory]
    [InlineData("opaque")]
    [InlineData("typed")]
    [InlineData("StoredProperty")]
    [InlineData("CreateHandler()")]
    [InlineData("mutable")]
    [InlineData("escaped")]
    [InlineData("assigned")]
    [InlineData("capturedHandler")]
    public async Task AnalyzeInvocation_UnknownHandlerTargets_AreAccepted(string handler)
    {
        var source = ApiSource + $$"""

        public static class Caller
        {
            public static Action<IReadOnlyList<int>> StoredProperty { get; set; } = Handle;

            private static void Handle(IReadOnlyList<int> values) { }

            private static Delegate CreateHandler() => (Action<IReadOnlyList<int>>)Handle;

            private static void Escape(ref Action<IReadOnlyList<int>> handler) { }

            private static void Replace(out Action<IReadOnlyList<int>> handler) => handler = Handle;

            public static void Run(
                IRequestBatcher<int> batcher,
                Delegate opaque,
                Action<IReadOnlyList<int>> typed)
            {
                Action<IReadOnlyList<int>> mutable = (IReadOnlyList<int> values) => { };
                mutable = Handle;
                Action<IReadOnlyList<int>> escaped = values => { };
                Escape(ref escaped);
                Replace(out Action<IReadOnlyList<int>> assigned);
                Action<IReadOnlyList<int>> capturedHandler = values => { };
                Action reassign = () => capturedHandler = Handle;
                reassign();
                batcher.ExecuteAsync(1, {{handler}});
            }
        }
        """;

        var diagnostics = await AnalyzeAsync(source);

        Assert.Empty(diagnostics);
    }

    [Theory]
    [InlineData("taskReturningParameter")]
    [InlineData("contravariantParameter")]
    [InlineData("TaskReturningField")]
    [InlineData("ContravariantField")]
    public async Task AnalyzeInvocation_UnknownVariantDelegateTypes_AreAccepted(string handler)
    {
        var source = ApiSource + $$"""

        public static class Caller
        {
            private static readonly Func<IReadOnlyList<int>, object> TaskReturningField =
                (Func<IReadOnlyList<int>, Task>)(static values => Task.CompletedTask);
            private static readonly Action<List<int>> ContravariantField =
                (Action<IReadOnlyList<int>>)(static values => { });

            public static void Run(
                IRequestBatcher<int> batcher,
                Func<IReadOnlyList<int>, object> taskReturningParameter,
                Action<List<int>> contravariantParameter)
            {
                batcher.ExecuteAsync(1, {{handler}});
            }
        }
        """;

        var diagnostics = await AnalyzeAsync(source);

        Assert.Empty(diagnostics);
    }

    [Theory]
    [InlineData("Action<int>")]
    [InlineData("Func<IReadOnlyList<int>, int>")]
    public async Task AnalyzeInvocation_UnknownTargetWithInvalidDelegateType_ReportsSignatureError(string delegateType)
    {
        var source = ApiSource + $$"""

        public static class Caller
        {
            public static void Run(IRequestBatcher<int> batcher, {{delegateType}} handler)
            {
                batcher.ExecuteAsync(1, handler);
            }
        }
        """;

        var diagnostics = await AnalyzeAsync(source);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("RB0002", diagnostic.Id);
    }

    [Fact]
    public async Task AnalyzeInvocation_FieldWithInvalidDelegateType_ReportsSignatureError()
    {
        const string source = ApiSource + """

        public static class Caller
        {
            private static readonly Action<int> StoredHandler = static value => { };

            public static void Run(IRequestBatcher<int> batcher)
            {
                batcher.ExecuteAsync(1, StoredHandler);
            }
        }
        """;

        var diagnostics = await AnalyzeAsync(source);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("RB0002", diagnostic.Id);
    }

    [Fact]
    public async Task AnalyzeInvocation_ReadonlyFieldTargetRemainsOpaque_IsAccepted()
    {
        const string source = ApiSource + """

        public sealed class Caller
        {
            private static readonly Action<IReadOnlyList<int>> StoredHandler = new Caller().Handle;

            private void Handle(IReadOnlyList<int> values) { }

            public static void Run(IRequestBatcher<int> batcher)
            {
                batcher.ExecuteAsync(1, StoredHandler);
            }
        }
        """;

        var diagnostics = await AnalyzeAsync(source);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task AnalyzeInvocation_UnrelatedTypesAndMethods_AreIgnored()
    {
        const string source = ApiSource + """

        namespace Nested.RequestBatcher
        {
            public static class RequestBatcherDelegateExtensions
            {
                public static System.Threading.Tasks.Task ExecuteAsync<T>(
                    this IRequestBatcher<T> batcher,
                    T request,
                    Delegate handler) => System.Threading.Tasks.Task.CompletedTask;
            }

            public interface IRequestBatcher<T>
            {
            }
        }

        public static class Caller
        {
            public static void Run(
                OtherNamespace.IRequestBatcher<int> other,
                Nested.RequestBatcher.IRequestBatcher<int> nested)
            {
                OtherNamespace.RequestBatcherDelegateExtensions.ExecuteAsync(
                    other,
                    1,
                    (Delegate)((int value) => { }));
                Nested.RequestBatcher.RequestBatcherDelegateExtensions.ExecuteAsync(
                    nested,
                    1,
                    (Delegate)((int value) => { }));
                RequestBatcher.OtherExtensions.ExecuteAsync(
                    new RequestBatcher.OtherBatcher<int>(),
                    1,
                    (Delegate)((int value) => { }));
            }
        }
        """;

        var diagnostics = await AnalyzeAsync(source);

        Assert.Empty(diagnostics);
    }

    [Theory]
    [InlineData("static (IReadOnlyList<int> values) => { }")]
    [InlineData("static (IReadOnlyList<int> values) => Task.CompletedTask")]
    [InlineData("static (IReadOnlyList<int> values) => ValueTask.CompletedTask")]
    [InlineData("static async (IReadOnlyList<int> values) => await Task.Yield()")]
    [InlineData("static async ValueTask (IReadOnlyList<int> values) => await Task.Yield()")]
    [InlineData("static (IReadOnlyList<int> values, CancellationToken token) => { }")]
    [InlineData("static (IReadOnlyList<int> values, IServiceProvider provider, CancellationToken token, IDisposable service) => { }")]
    [InlineData("static (IReadOnlyList<int> values, [FromKeyedServices(\"store\")] IDisposable service) => { }")]
    [InlineData("static (IReadOnlyList<int> values, [FromKeyedServices(null)] IDisposable service) => { }")]
    [InlineData("static (IReadOnlyList<int> values, [Microsoft.AspNetCore.Mvc.FromServices] IDisposable service) => { }")]
    [InlineData("(Action<IReadOnlyList<int>>)(static values => { })")]
    public async Task AnalyzeInvocation_SupportedStaticSignatures_AreAccepted(string handler)
    {
        var source = ApiSource + $$"""

        public static class Caller
        {
            public static void Run(IRequestBatcher<int> batcher)
            {
                batcher.ExecuteAsync(1, {{handler}});
            }
        }
        """;

        var diagnostics = await AnalyzeAsync(source);

        Assert.Empty(diagnostics);
    }

    [Theory]
    [InlineData("static () => { }", "")]
    [InlineData("handler: (Delegate)(static () => { })", "")]
    [InlineData("static (int value) => { }", "")]
    [InlineData("static (IEnumerable<int> values) => { }", "")]
    [InlineData("static (List<int> values) => { }", "")]
    [InlineData("static (IReadOnlyList<string> values) => { }", "")]
    [InlineData("static (IReadOnlyList<int> values) => 1", "")]
    [InlineData("static (IReadOnlyList<int> values) => Task.FromResult(1)", "")]
    [InlineData(
        "(Func<IReadOnlyList<int>, Task>)(Func<IReadOnlyList<int>, DerivedTask>)(static (IReadOnlyList<int> values) => new DerivedTask())",
        "public sealed class DerivedTask : Task { public DerivedTask() : base(static () => { }) { } }")]
    [InlineData("static (IReadOnlyList<int> values) => new ValueTask<int>(1)", "")]
    [InlineData("(Action<IReadOnlyList<int>>)(static async values => await Task.Yield())", "")]
    [InlineData("static (IReadOnlyList<int> values, CancellationToken first, CancellationToken second) => { }", "")]
    [InlineData("static (IReadOnlyList<int> values, int count = 1) => { }", "")]
    [InlineData("static (IReadOnlyList<int> values, CancellationToken token = default) => { }", "")]
    [InlineData("static (IReadOnlyList<int> values = null) => { }", "")]
    [InlineData("static (IReadOnlyList<int> values, [System.Runtime.InteropServices.Optional] int count) => { }", "")]
    [InlineData("static (ref IReadOnlyList<int> values) => { }", "")]
    [InlineData("static (IReadOnlyList<int> values, ref int count) => { }", "")]
    [InlineData("static (IReadOnlyList<int> values, in int count) => { }", "")]
    [InlineData("static (IReadOnlyList<int> values, out int count) => { count = 0; }", "")]
    [InlineData("static (IReadOnlyList<int> values, int* pointer) => { }", "")]
    [InlineData("static (IReadOnlyList<int> values, delegate*<void> callback) => { }", "")]
    [InlineData("static (IReadOnlyList<int> values, Span<int> span) => { }", "")]
    [InlineData("static ([FromKeyedServices(\"store\")] IReadOnlyList<int> values) => { }", "")]
    [InlineData("static (IReadOnlyList<int> values, [FromKeyedServices(\"store\")] CancellationToken token) => { }", "")]
    [InlineData("static (IReadOnlyList<int> values, [FromKeyedServices] IDisposable service) => { }", "")]
    [InlineData("(OptionalHandler)(static (IReadOnlyList<int> values, int count) => { })", "public delegate void OptionalHandler(IReadOnlyList<int> values, int count = 1);")]
    [InlineData("(Action<IReadOnlyList<int>, int>)(static (IReadOnlyList<int> values, int count = 1) => { })", "")]
    [InlineData("(RefResult)(static (IReadOnlyList<int> values) => ref Caller.Value)", "public delegate ref int RefResult(IReadOnlyList<int> values);")]
    [InlineData("(RefResult)(static (IReadOnlyList<int> values) => ref Caller.Value)", "public delegate ref readonly int RefResult(IReadOnlyList<int> values);")]
    public async Task AnalyzeInvocation_UnsupportedStaticSignatures_ReportSignatureError(
        string handler,
        string declarations)
    {
        var source = ApiSource + $$"""

        {{declarations}}

        public static class Caller
        {
            public static int Value;

            public static {{(handler.Contains('*') ? "unsafe " : "")}}void Run(IRequestBatcher<int> batcher)
            {
                batcher.ExecuteAsync(1, {{handler}});
            }
        }
        """;

        var diagnostics = await AnalyzeAsync(source);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("RB0002", diagnostic.Id);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.StartsWith("Invalid delegate handler signature: ", diagnostic.GetMessage());
        Assert.Equal(handler, source.Substring(diagnostic.Location.SourceSpan.Start, diagnostic.Location.SourceSpan.Length));
    }

    [Theory]
    [InlineData("batcher.ExecuteAsync(1, static (IReadOnlyList<string> values) => { });")]
    [InlineData("batcher.ExecuteAsync(new[] { 1 }, static (IReadOnlyList<string> values) => { });")]
    [InlineData("responseBatcher.ExecuteAsync(1, static (IReadOnlyList<RequestBatchEntry<int, object>> entries) => { });")]
    [InlineData("responseBatcher.ExecuteAsync(new[] { 1 }, static (IReadOnlyList<RequestBatchEntry<int, object>> entries) => { });")]
    [InlineData("responseBatcher.ExecuteAsync(1, static (IReadOnlyList<int> entries) => { });")]
    [InlineData("responseBatcher.ExecuteAsync(1, static (IReadOnlyList<RequestBatchEntry<string, string>> entries) => { });")]
    public async Task AnalyzeInvocation_IncorrectBatchTypesAcrossOverloads_ReportSignatureError(string invocation)
    {
        var source = ApiSource + $$"""

        public static class Caller
        {
            public static void Run(IRequestBatcher<int> batcher, IRequestBatcher<int, string> responseBatcher)
            {
                {{invocation}}
            }
        }
        """;

        var diagnostics = await AnalyzeAsync(source);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("RB0002", diagnostic.Id);
        Assert.Contains("The first parameter must be", diagnostic.GetMessage());
    }

    [Fact]
    public async Task AnalyzeInvocation_AliasesNullableTypesAndGenericCallers_AreAccepted()
    {
        const string source = ApiSource + """

        public static class Caller
        {
            public static void Run<TRequest, TResponse>(
                IRequestBatcher<TRequest> batcher,
                IRequestBatcher<TRequest, TResponse> responseBatcher,
                TRequest request,
                IReadOnlyList<TRequest> requests,
                IRequestBatcher<string> textBatcher)
            {
                batcher.ExecuteAsync(request, static (IReadOnlyList<TRequest> values) => { });
                batcher.ExecuteAsync(requests, static (IReadOnlyList<TRequest> values) => { });
                responseBatcher.ExecuteAsync(request, static (IReadOnlyList<RequestBatchEntry<TRequest, TResponse>> entries) => { });
                responseBatcher.ExecuteAsync(requests, static (IReadOnlyList<RequestBatchEntry<TRequest, TResponse>> entries) => { });
                textBatcher.ExecuteAsync("text", (BatchHandler)(static (TextBatch values) => { }));
                textBatcher.ExecuteAsync("text", static (IReadOnlyList<string?>? values) => { });
            }
        }
        """;

        var diagnostics = await AnalyzeAsync(source);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task AnalyzeInvocation_DynamicAndObjectBatchTypes_AreAccepted()
    {
        const string source = ApiSource + """

        public static class Caller
        {
            public static void Run(
                IRequestBatcher<dynamic> dynamicBatcher,
                IRequestBatcher<object> objectBatcher,
                IRequestBatcher<dynamic, dynamic> responseBatcher)
            {
                dynamicBatcher.ExecuteAsync(new object(), static (IReadOnlyList<object> values) => { });
                objectBatcher.ExecuteAsync(new object(), static (IReadOnlyList<dynamic> values) => { });
                responseBatcher.ExecuteAsync(new object(), static (IReadOnlyList<RequestBatchEntry<object, object>> entries) => { });
            }
        }
        """;

        var diagnostics = await AnalyzeAsync(source);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task AnalyzeInvocation_CovariantBatchType_ReportsSignatureError()
    {
        const string source = ApiSource + """

        public static class Caller
        {
            public static void Run(IRequestBatcher<string> batcher)
            {
                batcher.ExecuteAsync("text", static (IReadOnlyList<object> values) => { });
            }
        }
        """;

        var diagnostics = await AnalyzeAsync(source);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("RB0002", diagnostic.Id);
    }

    [Fact]
    public async Task AnalyzeInvocation_MultipleSignatureIssues_ReportsFirstIssueOnly()
    {
        const string source = ApiSource + """

        public static class Caller
        {
            public static void Run(IRequestBatcher<int> batcher)
            {
                batcher.ExecuteAsync(1, static (int value, int count = 1) => Task.FromResult(1));
            }
        }
        """;

        var diagnostics = await AnalyzeAsync(source);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("RB0002", diagnostic.Id);
        Assert.Contains("The first parameter must be", diagnostic.GetMessage());
    }

    [Fact]
    public async Task AnalyzeInvocation_IncompleteLambda_DoesNotCrashAnalyzer()
    {
        const string source = ApiSource + """

        public static class Caller
        {
            public static void Run(IRequestBatcher<int> batcher)
            {
                batcher.ExecuteAsync(1, static (MissingBatch values) => MissingResult());
            }
        }
        """;

        var diagnostics = await AnalyzeAsync(source, allowCompilerErrors: true);

        Assert.DoesNotContain(diagnostics, static diagnostic => diagnostic.Id == "AD0001");
    }

    private static async Task<ImmutableArray<Diagnostic>> AnalyzeAsync(string source, bool allowCompilerErrors = false)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(
            source,
            new CSharpParseOptions(LanguageVersion.CSharp12));
        var compilation = CSharpCompilation.Create(
            assemblyName: "StaticDelegateHandlerAnalyzerTests",
            syntaxTrees: [syntaxTree],
            references: GetReferences(),
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

        var compilationErrors = compilation.GetDiagnostics()
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ToArray();
        if (!allowCompilerErrors)
        {
            Assert.Empty(compilationErrors);
        }

        var analyzers = ImmutableArray.Create<DiagnosticAnalyzer>(new StaticDelegateHandlerAnalyzer());
        var result = compilation.WithAnalyzers(analyzers);
        return await result.GetAnalyzerDiagnosticsAsync();
    }

    private static IEnumerable<MetadataReference> GetReferences()
    {
        var paths = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))?
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            ?? throw new InvalidOperationException("Trusted platform assemblies were not available.");

        return paths.Select(static path => MetadataReference.CreateFromFile(path));
    }

    private const string ApiSource = """
        using System;
        using System.Collections.Generic;
        using System.Threading;
        using System.Threading.Tasks;
        using RequestBatcher;
        using Microsoft.Extensions.DependencyInjection;
        using TextBatch = System.Collections.Generic.IReadOnlyList<string>;
        using BatchHandler = System.Action<System.Collections.Generic.IReadOnlyList<string>>;

        namespace Microsoft.AspNetCore.Mvc
        {
            [AttributeUsage(AttributeTargets.Parameter)]
            public sealed class FromServicesAttribute : Attribute
            {
            }
        }

        namespace RequestBatcher
        {
            public interface IRequestBatcher<TRequest>
            {
                Task ExecuteAsync(TRequest request);
            }

            public interface IRequestBatcher<TRequest, TResponse>
            {
            }

            public sealed class RequestBatchEntry<TRequest, TResponse>
            {
            }

            public static class RequestBatcherDelegateExtensions
            {
                public static Task ExecuteAsync<TRequest>(
                    this IRequestBatcher<TRequest> batcher,
                    TRequest request,
                    Delegate handler,
                    CancellationToken cancellationToken = default) => Task.CompletedTask;

                public static Task ExecuteAsync<TRequest>(
                    this IRequestBatcher<TRequest> batcher,
                    IEnumerable<TRequest> requests,
                    Delegate handler,
                    CancellationToken cancellationToken = default) => Task.CompletedTask;

                public static Task<TResponse> ExecuteAsync<TRequest, TResponse>(
                    this IRequestBatcher<TRequest, TResponse> batcher,
                    TRequest request,
                    Delegate handler,
                    CancellationToken cancellationToken = default) => Task.FromResult(default(TResponse)!);

                public static Task<IReadOnlyList<TResponse>> ExecuteAsync<TRequest, TResponse>(
                    this IRequestBatcher<TRequest, TResponse> batcher,
                    IEnumerable<TRequest> requests,
                    Delegate handler,
                    CancellationToken cancellationToken = default) =>
                    Task.FromResult<IReadOnlyList<TResponse>>(Array.Empty<TResponse>());
            }

            public static class OtherExtensions
            {
                public static Task ExecuteAsync<TRequest>(
                    this OtherBatcher<TRequest> batcher,
                    TRequest request,
                    Delegate handler) => Task.CompletedTask;
            }

            public sealed class OtherBatcher<TRequest>
            {
            }
        }

        namespace OtherNamespace
        {
            public static class RequestBatcherDelegateExtensions
            {
                public static Task ExecuteAsync<TRequest>(
                    this IRequestBatcher<TRequest> batcher,
                    TRequest request,
                    Delegate handler) => Task.CompletedTask;
            }

            public interface IRequestBatcher<TRequest>
            {
            }
        }
        """;
}
