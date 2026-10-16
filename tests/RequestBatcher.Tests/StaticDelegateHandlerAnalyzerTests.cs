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
                batcher.ExecuteAsync(1, static (int value) => { });
                RequestBatcher.RequestBatcherDelegateExtensions.ExecuteAsync(
                    batcher: batcher,
                    requests: new[] { 1 },
                    handler: (((Delegate)((static (IReadOnlyList<int> values) => { })))),
                    cancellationToken: default);
                RequestBatcher.RequestBatcherDelegateExtensions.ExecuteAsync(
                    batcher,
                    request: 1,
                    handler: (static (int value) => { }));
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

        Assert.DoesNotContain(diagnostics, static diagnostic => diagnostic.Id == "RB0001");
    }

    [Fact]
    public async Task AnalyzeInvocation_NonStaticHandlersAndDelegateValues_ReportDiagnostics()
    {
        const string source = ApiSource + """

        public static class Caller
        {
            private static void Handle(int value)
            {
            }

            public static void Run(
                RequestBatcher.IRequestBatcher<int> batcher,
                OtherNamespace.IRequestBatcher<int> unrelated)
            {
                var captured = 1;
                RequestBatcher.RequestBatcherDelegateExtensions.ExecuteAsync(
                    batcher,
                    1,
                    (Delegate)((int value) => _ = captured));
                RequestBatcher.RequestBatcherDelegateExtensions.ExecuteAsync(
                    batcher,
                    1,
                    (Delegate)((int value) => { }));
                RequestBatcher.RequestBatcherDelegateExtensions.ExecuteAsync(
                    batcher,
                    1,
                    (Delegate)(static delegate(int value) { }));
                Delegate variable = static (int value) => { };
                RequestBatcher.RequestBatcherDelegateExtensions.ExecuteAsync(batcher, 1, variable);
                RequestBatcher.RequestBatcherDelegateExtensions.ExecuteAsync(batcher, 1, new Action<int>(Handle));
                RequestBatcher.RequestBatcherDelegateExtensions.ExecuteAsync(
                    handler: (Delegate)((int value) => { }),
                    request: 1,
                    batcher: batcher);
                OtherNamespace.RequestBatcherDelegateExtensions.ExecuteAsync(
                    unrelated,
                    1,
                    (Delegate)((int value) => { }));
            }
        }
        """;

        var diagnostics = await AnalyzeAsync(source);

        Assert.Equal(6, diagnostics.Count(static diagnostic => diagnostic.Id == "RB0001"));
        Assert.All(
            diagnostics.Where(static diagnostic => diagnostic.Id == "RB0001"),
            static diagnostic => Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity));
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

        Assert.Empty(diagnostics.Where(static diagnostic => diagnostic.Id == "RB0001"));
    }

    private static async Task<ImmutableArray<Diagnostic>> AnalyzeAsync(string source)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(
            source,
            new CSharpParseOptions(LanguageVersion.CSharp12));
        var compilation = CSharpCompilation.Create(
            assemblyName: "StaticDelegateHandlerAnalyzerTests",
            syntaxTrees: [syntaxTree],
            references: GetReferences(),
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var compilationErrors = compilation.GetDiagnostics()
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ToArray();
        Assert.Empty(compilationErrors);

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
