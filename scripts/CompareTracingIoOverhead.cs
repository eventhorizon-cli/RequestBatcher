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
            Console.WriteLine("CompareTracingIoOverhead [artifact-parent]\nCompares published 0.2.4 and 0.3.2 with identical 20 ms simulated downstream work. The 0.2.4 baseline runs two Disabled cases; 0.3.2 runs Disabled, Sample0, Sample1, Sample10, and Sample100. Retains package hashes and BenchmarkDotNet reports.");
            return 0;
        }

        if (args.Length > 1 || args.Any(argument => argument.StartsWith('-')))
        {
            Console.Error.WriteLine("Invalid arguments. Use --help.");
            return 2;
        }

        try
        {
            var repo = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(SourceFile())!, ".."));
            var parent = Path.GetFullPath(args.FirstOrDefault() ?? Path.Combine(Path.GetTempPath(), "requestbatcher-tracing-io"));
            var artifacts = Path.Combine(parent, $"run-{Guid.NewGuid():N}");
            Directory.CreateDirectory(artifacts);
            Console.WriteLine($"Artifacts: {artifacts}");
            foreach (var version in new[] { "0.2.4", "0.3.2" })
            {
                var projectDirectory = Path.Combine(artifacts, version);
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
                        <PackageReference Include="RequestBatcher" Version="{version}" />
                      </ItemGroup>
                    </Project>
                    """);
                await RunAsync(projectDirectory, artifacts, ["restore", project]).ConfigureAwait(false);
                using (var assets = JsonDocument.Parse(File.ReadAllText(Path.Combine(projectDirectory, "obj", "project.assets.json"))))
                {
                    if (!assets.RootElement.GetProperty("libraries").TryGetProperty($"RequestBatcher/{version}", out _))
                    {
                        throw new InvalidOperationException($"Expected RequestBatcher {version} in resolved assets.");
                    }
                }

                var package = Path.Combine(artifacts, "packages", "requestbatcher", version, $"requestbatcher.{version}.nupkg");
                File.WriteAllText(Path.Combine(projectDirectory, "package.sha512"),
                    $"{Convert.ToHexString(SHA512.HashData(File.ReadAllBytes(package))).ToLowerInvariant()}  {package}\n");
                await RunAsync(projectDirectory, artifacts,
                    ["run", "--project", project, "--configuration", "Release", "--no-restore", "--",
                        "--filter", version == "0.2.4" ? "*Disabled*" : "*TracingIoOverheadBenchmarks*",
                        "--artifacts", Path.Combine(projectDirectory, "results")]).ConfigureAwait(false);
                var reports = Directory.GetFiles(Path.Combine(projectDirectory, "results"), "*-report.csv", SearchOption.AllDirectories);
                var expectedRows = version == "0.2.4" ? 2 : 10;
                var reportRows = reports.Sum(path =>
                    File.ReadLines(path).Skip(1).Count(line => !string.IsNullOrWhiteSpace(line)));
                if (reports.Length == 0 || reportRows != expectedRows ||
                    reports.Any(path => File.ReadAllText(path).Contains("NA", StringComparison.Ordinal)))
                {
                    throw new InvalidOperationException(
                        $"Missing or failed benchmark results for {version}: found {reportRows} data rows, expected {expectedRows}.");
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

    private static string SourceFile([CallerFilePath] string path = "") => path;
}
