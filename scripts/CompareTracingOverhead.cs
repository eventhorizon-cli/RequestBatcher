#:property TargetFramework=net10.0
#:property PublishAot=false
#:property LangVersion=12.0

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;

internal static class CompareTracingOverhead
{
    private static async Task<int> Main(string[] args)
    {
        if (args is ["--help"])
        {
            Console.WriteLine("CompareTracingOverhead [artifact-parent] [--prepare-only]\nDownloads RequestBatcher 0.2.4 and compares it with the current source. TRACING_OVERHEAD_FILTER selects BenchmarkDotNet cases. --prepare-only restores and verifies the baseline without running benchmarks.");
            return 0;
        }

        var positional = args.Where(argument => argument != "--prepare-only").ToArray();
        if (positional.Length > 1 || positional.Any(argument => argument.StartsWith('-'))
            || args.Count(argument => argument == "--prepare-only") > 1)
        {
            Console.Error.WriteLine("Invalid arguments. Use --help.");
            return 2;
        }

        try
        {
            var repo = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(SourceFile())!, ".."));
            var parent = Path.GetFullPath(positional.FirstOrDefault() ?? Path.Combine(Path.GetTempPath(), "requestbatcher-tracing-overhead"));
            var artifacts = Path.Combine(parent, $"run-{Guid.NewGuid():N}");
            var baseline = Path.Combine(artifacts, "baseline-project");
            CopyDirectory(Path.Combine(repo, "tests", "RequestBatcher.Benchmarks"), baseline);
            foreach (var name in new[] { "global.json", "Directory.Build.props" })
            {
                File.Copy(Path.Combine(repo, name), Path.Combine(baseline, name));
            }

            var project = Path.Combine(baseline, "RequestBatcher.Benchmarks.csproj");
            var source = File.ReadAllText(project);
            const string reference = "<ProjectReference Include=\"../../src/RequestBatcher/RequestBatcher.csproj\" />";
            if (source.Split(reference, StringSplitOptions.None).Length != 2)
            {
                throw new InvalidOperationException("Expected exactly one RequestBatcher project reference.");
            }

            File.WriteAllText(project, source.Replace(reference, "<PackageReference Include=\"RequestBatcher\" Version=\"0.2.4\" />"));
            var packages = Path.Combine(artifacts, "nuget-package-024");
            var httpCache = Path.Combine(artifacts, "nuget-http-cache");
            Console.WriteLine($"Artifacts: {artifacts}");
            await RunAsync(repo, packages, httpCache, ["restore", project]).ConfigureAwait(false);
            var assetsPath = Path.Combine(baseline, "obj", "project.assets.json");
            using (var assets = JsonDocument.Parse(File.ReadAllText(assetsPath)))
            {
                if (!assets.RootElement.GetProperty("libraries").TryGetProperty("RequestBatcher/0.2.4", out _))
                {
                    throw new InvalidOperationException("The baseline did not resolve RequestBatcher 0.2.4.");
                }
            }

            var package = Path.Combine(packages, "requestbatcher", "0.2.4", "requestbatcher.0.2.4.nupkg");
            var checksum = Convert.ToHexString(SHA512.HashData(File.ReadAllBytes(package))).ToLowerInvariant();
            File.WriteAllText(Path.Combine(artifacts, "baseline-package.sha512"), $"{checksum}  {package}{Environment.NewLine}");
            Console.WriteLine($"Baseline SHA-512: {checksum}\nAssets: {assetsPath}");
            if (args.Contains("--prepare-only"))
            {
                return 0;
            }

            var filter = Environment.GetEnvironmentVariable("TRACING_OVERHEAD_FILTER") ?? "*TracingOverheadBenchmarks*";
            await RunAsync(repo, packages, httpCache,
                ["run", "--project", project, "--configuration", "Release", "--", "--filter", filter,
                    "--artifacts", Path.Combine(artifacts, "baseline")]).ConfigureAwait(false);
            await RunAsync(repo, Path.Combine(artifacts, "nuget-current"), httpCache,
                ["run", "--project", Path.Combine(repo, "tests", "RequestBatcher.Benchmarks", "RequestBatcher.Benchmarks.csproj"),
                    "--configuration", "Release", "--", "--filter", filter,
                    "--artifacts", Path.Combine(artifacts, "current")]).ConfigureAwait(false);
            Console.WriteLine($"Baseline artifacts: {Path.Combine(artifacts, "baseline")}\nCurrent artifacts: {Path.Combine(artifacts, "current")}");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    private static async Task RunAsync(string repo, string packages, string httpCache, string[] arguments)
    {
        var start = new ProcessStartInfo("dotnet") { WorkingDirectory = repo, UseShellExecute = false };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment["NUGET_PACKAGES"] = packages;
        start.Environment["NUGET_HTTP_CACHE_PATH"] = httpCache;
        start.Environment["NuGetAudit"] = "false";
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start dotnet.");
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromHours(2));
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

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        }

        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            var name = Path.GetFileName(directory);
            if (name is not ("bin" or "obj"))
            {
                CopyDirectory(directory, Path.Combine(destination, name));
            }
        }
    }

    private static string SourceFile([CallerFilePath] string path = "") => path;
}
