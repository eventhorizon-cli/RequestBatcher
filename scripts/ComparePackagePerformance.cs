#:property TargetFramework=net10.0
#:property PublishAot=false
#:property LangVersion=12.0

using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

internal static class ComparePackagePerformance
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static async Task<int> Main(string[] args)
    {
        if (args is ["--help"])
        {
            Console.WriteLine("ComparePackagePerformance <baseline-version> <candidate-version> [artifact-parent] [--prepare-only]\nCompares exact stable NuGet packages using identical .NET 10 response workloads (typed/delegate; single/explicit/mixed; zero/20 ms downstream delay). Runs two rounds in opposite version order, one process per package per round, three warmups and eight adaptive 200 ms measurement iterations. Retains isolated projects, package/source hashes, logs, raw reports, samples.csv, and comparison.csv. --prepare-only restores and builds both projects without measuring.");
            return 0;
        }

        var positional = args.Where(argument => argument != "--prepare-only").ToArray();
        const string versionPattern = @"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$";
        if (positional.Length is < 2 or > 3 || positional.Any(argument => argument.StartsWith('-')) ||
            args.Count(argument => argument == "--prepare-only") > 1 ||
            !Regex.IsMatch(positional[0], versionPattern) || !Regex.IsMatch(positional[1], versionPattern) ||
            positional[0] == positional[1])
        {
            Console.Error.WriteLine("Specify two different stable major.minor.patch versions. Use --help.");
            return 2;
        }

        try
        {
            var repo = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(SourceFile())!, ".."));
            var parent = Path.GetFullPath(positional.ElementAtOrDefault(2) ??
                Path.Combine(Path.GetTempPath(), "requestbatcher-package-performance"));
            var artifacts = Path.Combine(parent, $"run-{Guid.NewGuid():N}");
            Directory.CreateDirectory(artifacts);
            File.Copy(SourceFile(), Path.Combine(artifacts, "ComparePackagePerformance.cs"));
            Console.WriteLine($"Artifacts: {artifacts}");
            File.WriteAllText(Path.Combine(artifacts, "environment.json"), JsonSerializer.Serialize(new
            {
                CreatedUtc = DateTimeOffset.UtcNow,
                Runtime = RuntimeInformation.FrameworkDescription,
                OS = RuntimeInformation.OSDescription,
                Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                Environment.ProcessorCount,
                Baseline = positional[0],
                Candidate = positional[1],
                RequestsPerWave = 256,
                DownstreamDelayMilliseconds = new[] { 0, 20 },
                Rounds = 2,
                Iterations = 8,
                Warmups = 3,
                // This is BenchmarkDotNet's target measurement duration, not the simulated downstream delay.
                IterationMilliseconds = 200,
            }, JsonOptions));
            await RunAsync(repo, artifacts, "dotnet-info", ["--info"]).ConfigureAwait(false);

            var benchmarkProject = XDocument.Load(Path.Combine(repo, "tests", "RequestBatcher.Benchmarks", "RequestBatcher.Benchmarks.csproj"));
            string PackageVersion(string name) => benchmarkProject.Descendants("PackageReference")
                .Single(element => (string?)element.Attribute("Include") == name).Attribute("Version")!.Value;
            var versions = positional.Take(2).ToArray();
            foreach (var version in versions)
            {
                var directory = Path.Combine(artifacts, version);
                Directory.CreateDirectory(directory);
                foreach (var file in new[] { "global.json", "Directory.Build.props" })
                {
                    File.Copy(Path.Combine(repo, file), Path.Combine(directory, file));
                }

                var workload = Path.Combine(directory, "PackageComparisonBenchmarks.cs");
                File.Copy(Path.Combine(repo, "tests", "RequestBatcher.Benchmarks", "InMemory", "PackageComparisonBenchmarks.cs"), workload);
                File.WriteAllText(Path.Combine(directory, "workload.sha256"),
                    $"{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(workload))).ToLowerInvariant()}  PackageComparisonBenchmarks.cs\n");
                File.WriteAllText(Path.Combine(directory, "PackageComparison.csproj"), $"""
                    <Project Sdk="Microsoft.NET.Sdk">
                      <PropertyGroup>
                        <OutputType>Exe</OutputType>
                        <TargetFramework>net10.0</TargetFramework>
                      </PropertyGroup>
                      <ItemGroup>
                        <PackageReference Include="BenchmarkDotNet" Version="{PackageVersion("BenchmarkDotNet")}" />
                        <PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="{PackageVersion("Microsoft.Extensions.DependencyInjection")}" />
                        <PackageReference Include="RequestBatcher" Version="[{version}]" />
                      </ItemGroup>
                    </Project>
                    """);
                File.WriteAllText(Path.Combine(directory, "Program.cs"), """
                    using BenchmarkDotNet.Configs;
                    using BenchmarkDotNet.Exporters.Json;
                    using BenchmarkDotNet.Jobs;
                    using BenchmarkDotNet.Running;
                    using Perfolizer.Horology;
                    using RequestBatcher.Benchmarks.InMemory;

                    // The 200 ms value controls how long BenchmarkDotNet measures each operation; the workload itself
                    // selects its 0 ms and 20 ms downstream-delay parameters.
                    var config = ManualConfig.Create(DefaultConfig.Instance)
                        .AddJob(Job.Default.WithId("PackageComparison")
                            .WithLaunchCount(1).WithWarmupCount(3).WithIterationCount(8)
                            .WithIterationTime(TimeInterval.FromMilliseconds(200)).WithUnrollFactor(1))
                        .AddExporter(JsonExporter.Full);
                    BenchmarkSwitcher.FromAssembly(typeof(PackageComparisonBenchmarks).Assembly).Run(args, config);
                    """);
                await RunAsync(directory, artifacts, $"restore-{version}", ["restore", "PackageComparison.csproj"]).ConfigureAwait(false);
                var assetsPath = Path.Combine(directory, "obj", "project.assets.json");
                using var assets = JsonDocument.Parse(File.ReadAllText(assetsPath));
                var library = assets.RootElement.GetProperty("libraries").EnumerateObject()
                    .Single(property => string.Equals(property.Name, $"RequestBatcher/{version}", StringComparison.OrdinalIgnoreCase)).Value;
                if (library.GetProperty("type").GetString() != "package")
                {
                    throw new InvalidOperationException($"Expected published RequestBatcher package {version}.");
                }

                var package = Path.Combine(artifacts, "packages", "requestbatcher", version, $"requestbatcher.{version}.nupkg");
                File.WriteAllText(Path.Combine(directory, "package.sha512"),
                    $"{Convert.ToHexString(SHA512.HashData(File.ReadAllBytes(package))).ToLowerInvariant()}  {package}\n");
                await RunAsync(directory, artifacts, $"build-{version}",
                    ["build", "PackageComparison.csproj", "--configuration", "Release", "--no-restore"]).ConfigureAwait(false);
            }

            if (args.Contains("--prepare-only"))
            {
                Console.WriteLine($"Prepared both exact package projects: {artifacts}");
                return 0;
            }

            var samples = new List<Sample>();
            var validations = new List<Validation>();
            for (var round = 1; round <= 2; round++)
            {
                foreach (var version in round == 1 ? versions : versions.Reverse())
                {
                    var directory = Path.Combine(artifacts, version);
                    var results = Path.Combine(directory, $"round-{round}");
                    await RunAsync(directory, artifacts, $"benchmark-{version}-round-{round}",
                        ["run", "--project", "PackageComparison.csproj", "--configuration", "Release", "--no-build", "--no-restore", "--",
                            "--filter", "*PackageComparisonBenchmarks*", "--artifacts", results]).ConfigureAwait(false);
                    var reports = Directory.GetFiles(results, "*-report-full.json", SearchOption.AllDirectories);
                    if (reports.Length != 1)
                    {
                        throw new InvalidDataException($"Expected one full report for {version}, round {round}.");
                    }

                    using var report = JsonDocument.Parse(File.ReadAllText(reports[0]));
                    var benchmarks = report.RootElement.GetProperty("Benchmarks");
                    if (benchmarks.GetArrayLength() != 12)
                    {
                        throw new InvalidDataException($"Expected twelve cases for {version}, round {round}.");
                    }

                    foreach (var benchmark in benchmarks.EnumerateArray())
                    {
                        var mean = benchmark.GetProperty("Statistics").GetProperty("Mean").GetDouble();
                        var allocated = benchmark.GetProperty("Memory").GetProperty("BytesAllocatedPerOperation").GetDouble();
                        if (!double.IsFinite(mean) || mean <= 0 || !double.IsFinite(allocated) || allocated <= 0)
                        {
                            throw new InvalidDataException("Benchmark returned missing or invalid measurements.");
                        }

                        samples.Add(new Sample(version, round, benchmark.GetProperty("Method").GetString()!,
                            benchmark.GetProperty("Parameters").GetString()!, mean, allocated));
                    }

                    var log = File.ReadAllText(Path.Combine(artifacts, $"benchmark-{version}-round-{round}.log"));
                    validations.AddRange(ValidateRun(log, version, round,
                        samples.Where(sample => sample.Version == version && sample.Round == round).ToArray()));
                }
            }

            if (validations.Select(validation => validation.Framework).Distinct(StringComparer.Ordinal).Count() != 1)
            {
                throw new InvalidDataException("The benchmark processes did not all run on the same .NET 10 runtime.");
            }

            File.WriteAllText(Path.Combine(artifacts, "validation.json"), JsonSerializer.Serialize(validations, JsonOptions));
            using (var writer = File.CreateText(Path.Combine(artifacts, "samples.csv")))
            {
                writer.WriteLine("Version,Round,Method,Parameters,MeanNanosecondsPerWave,AllocatedBytesPerWave,AllocatedBytesPerRequest");
                foreach (var sample in samples)
                {
                    writer.WriteLine(FormattableString.Invariant($"{sample.Version},{sample.Round},{sample.Method},{sample.Parameters},{sample.MeanNanoseconds:R},{sample.AllocatedBytes:R},{sample.AllocatedBytes / 256:R}"));
                }
            }

            using (var writer = File.CreateText(Path.Combine(artifacts, "comparison.csv")))
            {
                writer.WriteLine("Method,Parameters,BaselineMeanMicroseconds,CandidateMeanMicroseconds,TimeChangePercent,BaselineBytesPerRequest,CandidateBytesPerRequest,AllocationChangePercent");
                foreach (var group in samples.GroupBy(sample => (sample.Method, sample.Parameters)))
                {
                    var baseline = group.Where(sample => sample.Version == versions[0]).ToArray();
                    var candidate = group.Where(sample => sample.Version == versions[1]).ToArray();
                    if (baseline.Length != 2 || candidate.Length != 2)
                    {
                        throw new InvalidDataException("Each case must have two samples per package version.");
                    }

                    var beforeTime = baseline.Average(sample => sample.MeanNanoseconds);
                    var afterTime = candidate.Average(sample => sample.MeanNanoseconds);
                    var beforeAllocation = baseline.Average(sample => sample.AllocatedBytes);
                    var afterAllocation = candidate.Average(sample => sample.AllocatedBytes);
                    writer.WriteLine(FormattableString.Invariant($"{group.Key.Method},{group.Key.Parameters},{beforeTime / 1_000:F3},{afterTime / 1_000:F3},{(afterTime / beforeTime - 1) * 100:F2},{beforeAllocation / 256:F3},{afterAllocation / 256:F3},{(afterAllocation / beforeAllocation - 1) * 100:F2}"));
                }
            }

            Console.WriteLine($"Completed 48 validated benchmark case records across four benchmark processes. Comparison and raw evidence: {artifacts}");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static Validation[] ValidateRun(string log, string version, int round, Sample[] samples)
    {
        const string prefix = "Package comparison validation:";
        var records = new List<Validation>();
        foreach (var line in log.Split('\n').Where(line => line.StartsWith(prefix, StringComparison.Ordinal)))
        {
            var fields = line[prefix.Length..].Split(',').Select(field => field.Trim().Split('=', 2))
                .ToDictionary(pair => pair[0], pair => pair[1], StringComparer.Ordinal);
            var method = fields["Method"];
            var useDelegate = bool.Parse(fields["UseDelegate"]);
            var downstreamDelayMilliseconds = int.Parse(fields["DownstreamDelayMilliseconds"], CultureInfo.InvariantCulture);
            var framework = fields["Framework"];
            var waves = long.Parse(fields["Waves"], CultureInfo.InvariantCulture);
            var handlerCalls = long.Parse(fields["HandlerCalls"], CultureInfo.InvariantCulture);
            if (downstreamDelayMilliseconds is not (0 or 20) ||
                !framework.StartsWith(".NET 10.", StringComparison.Ordinal) || fields["RequestsPerWave"] != "256" ||
                waves <= 0 || handlerCalls < waves * 8 || handlerCalls > waves * 256 ||
                fields["ExactlyOnce"] != "True" || fields["ResponseOrder"] != "True" || fields["DispatchBounds"] != "True")
            {
                throw new InvalidDataException($"Invalid benchmark validation: {line}");
            }

            records.Add(new Validation(version, round, method, useDelegate, downstreamDelayMilliseconds, framework, waves, handlerCalls));
        }

        var expected = new HashSet<string>(StringComparer.Ordinal);
        foreach (var method in new[] { "SingleResponses", "ExplicitResponses", "MixedResponses" })
        {
            foreach (var delay in new[] { 0, 20 })
            {
                expected.Add($"{method}/Delay={delay}/UseDelegate=False");
                expected.Add($"{method}/Delay={delay}/UseDelegate=True");
            }
        }

        static string RecordKey(Validation record) =>
            $"{record.Method}/Delay={record.DownstreamDelayMilliseconds}/UseDelegate={record.UseDelegate}";

        static string SampleKey(Sample sample)
        {
            var parameters = sample.Parameters.Replace(" ", string.Empty, StringComparison.Ordinal)
                .Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(parameter => parameter.Split('=', 2))
                .ToDictionary(parts => parts[0], parts => parts[1], StringComparer.Ordinal);
            var delay = parameters["DownstreamDelayMilliseconds"];
            var useDelegate = parameters["UseDelegate"];
            return $"{sample.Method}/Delay={delay}/UseDelegate={useDelegate}";
        }

        var recorded = records.Select(RecordKey).ToArray();
        var sampleKeys = samples.Select(SampleKey).ToArray();
        if (records.Count != 12 || recorded.Distinct(StringComparer.Ordinal).Count() != 12 || !expected.SetEquals(recorded) ||
            !expected.SetEquals(sampleKeys))
        {
            throw new InvalidDataException($"Expected matching twelve-case report and validation coverage for {version}, round {round}.");
        }

        return records.ToArray();
    }

    private static async Task RunAsync(string directory, string artifacts, string name, string[] arguments)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment["NUGET_PACKAGES"] = Path.Combine(artifacts, "packages");
        start.Environment["NUGET_HTTP_CACHE_PATH"] = Path.Combine(artifacts, "http-cache");
        start.Environment["NuGetAudit"] = "false";
        File.AppendAllText(Path.Combine(artifacts, "commands.jsonl"), JsonSerializer.Serialize(new { name, directory, arguments }) + "\n");
        using var file = File.CreateText(Path.Combine(artifacts, $"{name}.log"));
        var writer = TextWriter.Synchronized(file);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start dotnet.");
        async Task PumpAsync(StreamReader reader)
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                writer.WriteLine(line);
                Console.WriteLine(line);
            }
        }

        var output = Task.WhenAll(PumpAsync(process.StandardOutput), PumpAsync(process.StandardError));
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(15));
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            await output.ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"{name} exited with {process.ExitCode}; see {name}.log.");
            }
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().ConfigureAwait(false);
            }

            await output.ConfigureAwait(false);
        }
    }

    private static string SourceFile([CallerFilePath] string path = "") => path;

    private sealed record Sample(string Version, int Round, string Method, string Parameters, double MeanNanoseconds, double AllocatedBytes);

    private sealed record Validation(string Version, int Round, string Method, bool UseDelegate, int DownstreamDelayMilliseconds, string Framework, long Waves, long HandlerCalls);
}
