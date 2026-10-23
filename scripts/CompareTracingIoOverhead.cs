#:property TargetFramework=net8.0
#:property PublishAot=false
#:property LangVersion=12.0

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;

internal static class CompareTracingIoOverhead
{
    private static async Task<int> Main(string[] args)
    {
        if (args is ["--help"])
        {
            Console.WriteLine("CompareTracingIoOverhead [--compare-source [--baseline-source path]] [artifact-parent]\nCompares published 0.2.4 and 0.3.2 with identical 20 ms simulated downstream work. The 0.2.4 baseline runs two Disabled cases; 0.3.2 runs Disabled, Sample0, Sample1, Sample10, and Sample100. With --compare-source, compares the 0.3.2 package with a source snapshot using the ten current-version cases. --baseline-source replaces that package baseline with a snapshot of the specified repository tree. Retains source snapshots, package hashes, and BenchmarkDotNet reports.");
            return 0;
        }

        var compareSource = false;
        string? baselineSource = null;
        string? parentArgument = null;
        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            if (argument == "--compare-source" && !compareSource)
            {
                compareSource = true;
                continue;
            }

            if (argument == "--baseline-source" && baselineSource is null && index + 1 < args.Length &&
                !args[index + 1].StartsWith('-'))
            {
                baselineSource = Path.GetFullPath(args[++index]);
                continue;
            }

            if (argument.StartsWith('-') || parentArgument is not null)
            {
                Console.Error.WriteLine("Invalid arguments. Use --help.");
                return 2;
            }

            parentArgument = argument;
        }

        if (baselineSource is not null && !compareSource)
        {
            Console.Error.WriteLine("--baseline-source requires --compare-source.");
            return 2;
        }

        try
        {
            var repo = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(SourceFile())!, ".."));
            var parent = Path.GetFullPath(parentArgument ?? Path.Combine(Path.GetTempPath(), "requestbatcher-tracing-io"));
            var artifacts = Path.Combine(parent, $"run-{Guid.NewGuid():N}");
            Directory.CreateDirectory(artifacts);
            Console.WriteLine($"Artifacts: {artifacts}");
            var sourceTree = compareSource ? Path.Combine(artifacts, "source-tree") : null;
            if (sourceTree is not null)
            {
                CreateSourceSnapshot(repo, sourceTree);
            }
            if (baselineSource is not null)
            {
                CreateSourceSnapshot(baselineSource, Path.Combine(artifacts, "baseline-source-tree"));
            }

            var scenarios = compareSource
                ? new[]
                {
                    baselineSource is null
                        ? new Scenario("0.3.2-package", "0.3.2", IsSource: false)
                        : new Scenario("baseline-source", PackageVersion: null, IsSource: true, "baseline-source-tree"),
                    new Scenario("source", PackageVersion: null, IsSource: true, "source-tree"),
                }
                : new[]
                {
                    new Scenario("0.2.4", "0.2.4", IsSource: false),
                    new Scenario("0.3.2", "0.3.2", IsSource: false),
                };

            foreach (var scenario in scenarios)
            {
                var projectDirectory = Path.Combine(artifacts, scenario.Name);
                Directory.CreateDirectory(projectDirectory);
                foreach (var file in new[] { "global.json", "Directory.Build.props" })
                {
                    File.Copy(Path.Combine(repo, file), Path.Combine(projectDirectory, file));
                }

                File.Copy(Path.Combine(repo, "tests", "RequestBatcher.Benchmarks", "InMemory", "TracingIoOverheadBenchmarks.cs"),
                    Path.Combine(projectDirectory, "TracingIoOverheadBenchmarks.cs"));
                File.WriteAllText(Path.Combine(projectDirectory, "Program.cs"),
                    "BenchmarkDotNet.Running.BenchmarkSwitcher.FromAssembly(typeof(RequestBatcher.Benchmarks.InMemory.TracingIoOverheadBenchmarks).Assembly).Run(args);\n");
                var project = Path.Combine(projectDirectory, "TracingIoComparison.csproj");
                var requestBatcherReference = scenario.IsSource
                    ? $"        <ProjectReference Include=\"../{scenario.SourceDirectory}/src/RequestBatcher/RequestBatcher.csproj\" />"
                    : $"        <PackageReference Include=\"RequestBatcher\" Version=\"{scenario.PackageVersion}\" />";
                File.WriteAllText(project, $"""
                    <Project Sdk="Microsoft.NET.Sdk">
                      <PropertyGroup>
                        <OutputType>Exe</OutputType>
                        <TargetFramework>net8.0</TargetFramework>
                      </PropertyGroup>
                      <ItemGroup>
                        <PackageReference Include="BenchmarkDotNet" Version="0.15.8" />
                        <PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="10.0.11" />
                        <PackageReference Include="OpenTelemetry" Version="1.20.1" />
                      {requestBatcherReference}
                      </ItemGroup>
                    </Project>
                    """);
                await RunAsync(projectDirectory, artifacts, ["restore", project]).ConfigureAwait(false);
                using (var assets = JsonDocument.Parse(File.ReadAllText(Path.Combine(projectDirectory, "obj", "project.assets.json"))))
                {
                    var libraries = assets.RootElement.GetProperty("libraries");
                    var requestBatcherLibrary = libraries.EnumerateObject()
                        .FirstOrDefault(property => property.Name.StartsWith("RequestBatcher/", StringComparison.OrdinalIgnoreCase));
                    if (requestBatcherLibrary.Value.ValueKind != JsonValueKind.Object ||
                        !requestBatcherLibrary.Value.TryGetProperty("type", out var type) ||
                        !string.Equals(type.GetString(), scenario.IsSource ? "project" : "package", StringComparison.OrdinalIgnoreCase) ||
                        (!scenario.IsSource &&
                         !string.Equals(requestBatcherLibrary.Name, $"RequestBatcher/{scenario.PackageVersion}", StringComparison.OrdinalIgnoreCase)))
                    {
                        throw new InvalidOperationException(
                            $"Expected RequestBatcher {scenario.IsSource switch { true => "project", false => $"package {scenario.PackageVersion}" }} in resolved assets.");
                    }
                }

                if (!scenario.IsSource)
                {
                    var package = Path.Combine(artifacts, "packages", "requestbatcher", scenario.PackageVersion!, $"requestbatcher.{scenario.PackageVersion}.nupkg");
                    File.WriteAllText(Path.Combine(projectDirectory, "package.sha512"),
                        $"{Convert.ToHexString(SHA512.HashData(File.ReadAllBytes(package))).ToLowerInvariant()}  {package}\n");
                }
                await RunAsync(projectDirectory, artifacts,
                    ["run", "--project", project, "--configuration", "Release", "--no-restore", "--",
                        "--filter", scenario.PackageVersion == "0.2.4" ? "*Disabled*" : "*TracingIoOverheadBenchmarks*",
                        "--artifacts", Path.Combine(projectDirectory, "results")]).ConfigureAwait(false);
                var reports = Directory.GetFiles(Path.Combine(projectDirectory, "results"), "*-report.csv", SearchOption.AllDirectories);
                var expectedRows = scenario.PackageVersion == "0.2.4" ? 2 : 10;
                var reportRows = reports.Sum(path =>
                    File.ReadLines(path).Skip(1).Count(line => !string.IsNullOrWhiteSpace(line)));
                if (reports.Length == 0 || reportRows != expectedRows ||
                    reports.Any(path => File.ReadAllText(path).Contains("NA", StringComparison.Ordinal)))
                {
                    throw new InvalidOperationException(
                        $"Missing or failed benchmark results for {scenario.Name}: found {reportRows} data rows, expected {expectedRows}.");
                }
            }

            Console.WriteLine($"Completed. Reports and package evidence: {artifacts}");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static async Task RunAsync(string directory, string artifacts, string[] arguments)
    {
        var start = new ProcessStartInfo("dotnet") { WorkingDirectory = directory, UseShellExecute = false };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment["NUGET_PACKAGES"] = Path.Combine(artifacts, "packages");
        start.Environment["NUGET_HTTP_CACHE_PATH"] = Path.Combine(artifacts, "http-cache");
        start.Environment["NuGetAudit"] = "false";
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start dotnet.");
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromHours(1));
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"dotnet {arguments[0]} failed with exit code {process.ExitCode}.");
            }
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().ConfigureAwait(false);
            }
        }
    }

    private static void CreateSourceSnapshot(string repo, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in new[] { "global.json", "Directory.Build.props" })
        {
            File.Copy(Path.Combine(repo, file), Path.Combine(destination, file));
        }

        foreach (var directory in new[] { "src/RequestBatcher", "src/RequestBatcher.Analyzers" })
        {
            CopyDirectoryWithoutBuildOutputs(Path.Combine(repo, directory), Path.Combine(destination, directory));
        }

        File.WriteAllText(
            Path.Combine(destination, "SNAPSHOT.txt"),
            "This source tree was copied before the benchmark run by CompareTracingIoOverhead.cs.\n" +
            "It is the complete RequestBatcher and RequestBatcher.Analyzers source snapshot used by the source scenario.\n");
    }

    private static void CopyDirectoryWithoutBuildOutputs(string source, string destination)
    {
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            if (relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(part => part is "bin" or "obj"))
            {
                continue;
            }

            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    private sealed record Scenario(string Name, string? PackageVersion, bool IsSource, string? SourceDirectory = null);

    private static string SourceFile([CallerFilePath] string path = "") => path;
}
