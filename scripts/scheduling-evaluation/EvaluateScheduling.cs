#:property TargetFramework=net8.0
#:property PublishAot=false
#:property LangVersion=12.0

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;

internal static class EvaluateScheduling
{
    private static async Task<int> Main(string[] args)
    {
        if (args is ["--help"] or ["-h"])
        {
            PrintHelp();
            return 0;
        }

        try
        {
            var options = ParseOptions(args);
            var repository = RepositoryRoot();
            var scratch = Path.Combine(Path.GetTempPath(), $"requestbatcher-scheduling-{Guid.NewGuid():N}");
            Directory.CreateDirectory(scratch);
            var output = Path.GetFullPath(options.Output);
            Directory.CreateDirectory(output);

            await PrepareAsync(repository, scratch).ConfigureAwait(false);
            var probe = Path.Combine(scratch, "Probe", "bin", "Release", "net8.0", "RequestBatcher.Benchmarks.dll");
            var environment = new Dictionary<string, object?>
            {
                ["Scratch"] = scratch,
                ["TieredCompilation"] = false,
                ["RegularCases"] = 33,
                ["BurstCases"] = 6,
                ["PartitionCounts"] = new[] { 1, 8 },
                ["PartitionPolicy"] = "Unkeyed cases omit UsePartitionKey; keyed cases use UsePartitionKey(selector, count)",
                ["SourceCommit"] = await RunTextAsync(repository, "git", "rev-parse", "HEAD").ConfigureAwait(false),
                ["SourceDiff"] = await RunTextAsync(repository, "git", "diff", "--", "src/RequestBatcher").ConfigureAwait(false),
                ["Sdk"] = await RunTextAsync(scratch, "dotnet", "--version").ConfigureAwait(false),
            };
            await File.WriteAllTextAsync(
                Path.Combine(output, "environment.json"),
                JsonSerializer.Serialize(environment, new JsonSerializerOptions { WriteIndented = true }) + "\n")
                .ConfigureAwait(false);
            Console.WriteLine($"Isolated source: {scratch}");
            if (options.PrepareOnly)
            {
                return 0;
            }

            var processEnvironment = new Dictionary<string, string?> { ["DOTNET_TieredCompilation"] = "0" };
            if (options.SelfCheck)
            {
                await RunCheckedAsync(scratch, processEnvironment, "dotnet", probe, "--offload-self-check")
                    .ConfigureAwait(false);
                return 0;
            }

            var cases = CreateCases();
            await using var measurements = new JsonLinesWriter(Path.Combine(output, "measurements.jsonl"));
            for (var index = 0; index < cases.Count; index++)
            {
                var result = await RunCheckedAsync(
                    scratch,
                    processEnvironment,
                    "dotnet",
                    new[] { probe }.Concat(cases[index].Select(static value => value.ToString() ?? string.Empty)).ToArray(),
                    TimeSpan.FromSeconds(180)).ConfigureAwait(false);
                await measurements.WriteAsync(result.Stdout).ConfigureAwait(false);
                using var document = JsonDocument.Parse(result.Stdout);
                var rate = document.RootElement.GetProperty("Measurements")
                    .EnumerateArray()
                    .Select(measurement => measurement.GetProperty("RequestsPerSecond").GetDouble())
                    .Order()
                    .ElementAt(1);
                Console.WriteLine($"{index + 1}/{cases.Count} [{string.Join(", ", cases[index])}]: {rate:F0} requests/s");
            }

            await using var bursts = new JsonLinesWriter(Path.Combine(output, "bursts.jsonl"));
            foreach (var count in new[] { 64, 2048 })
            {
                foreach (var (keys, partitions) in new[] { ("none", 1), ("uniform", 8), ("skew", 8) })
                {
                    var caseArguments = new object[] { "burst", 64, 8, partitions, false, 1, keys, count };
                    var result = await RunCheckedAsync(
                        scratch,
                        processEnvironment,
                        "dotnet",
                        new[] { probe }.Concat(caseArguments.Select(static value => value.ToString() ?? string.Empty)).ToArray(),
                        TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                    using (JsonDocument.Parse(result.Stdout))
                    {
                    }
                    await bursts.WriteAsync(result.Stdout).ConfigureAwait(false);
                    Console.WriteLine(result.Stdout.Trim());
                }
            }

            return 0;
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine(exception.Message);
            PrintHelp(Console.Error);
            return 2;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static async Task PrepareAsync(string repository, string scratch)
    {
        foreach (var name in new[] { "global.json", "Directory.Build.props", ".editorconfig" })
        {
            File.Copy(Path.Combine(repository, name), Path.Combine(scratch, name));
        }

        CopyDirectory(
            Path.Combine(repository, "src", "RequestBatcher"),
            Path.Combine(scratch, "src", "RequestBatcher"),
            static path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

        var dispatchLoop = Path.Combine(scratch, "src", "RequestBatcher", "Scheduling", "BatchDispatchLoop.cs");
        ReplaceOnce(
            dispatchLoop,
            "    private readonly RequestBatchConsumer<TRequest> _batchConsumer;",
            "    internal static bool EvaluationOffload;\n"
                + "    internal static TaskCompletionSource? EvaluationOffloadGate;\n"
                + "    internal static TaskCompletionSource? EvaluationOffloadEntered;\n"
                + "    private readonly RequestBatchConsumer<TRequest> _batchConsumer;");
        ReplaceOnce(
            dispatchLoop,
            "        var processing = _batchConsumer.ExecuteAsync(pendingRequests, cancellationToken);\n"
                + "        if (processing.IsCompletedSuccessfully)\n"
                + "        {\n"
                + "            CompleteExecution();\n"
                + "            _executionSlots.Release();\n"
                + "            return;\n"
                + "        }\n\n"
                + "        _ = ProcessBatchAsync(processing);",
            "        if (EvaluationOffload)\n"
                + "        {\n"
                + "            _ = Task.Run(() => ProcessOffloadedBatchAsync(pendingRequests, cancellationToken));\n"
                + "            return;\n"
                + "        }\n\n"
                + "        var processing = _batchConsumer.ExecuteAsync(pendingRequests, cancellationToken);\n"
                + "        if (processing.IsCompletedSuccessfully)\n"
                + "        {\n"
                + "            CompleteExecution();\n"
                + "            _executionSlots.Release();\n"
                + "            return;\n"
                + "        }\n\n"
                + "        _ = ProcessBatchAsync(processing);");
        ReplaceOnce(
            dispatchLoop,
            "    private async Task ProcessBatchAsync(ValueTask processing)",
            "    private async Task ProcessOffloadedBatchAsync(\n"
                + "        IReadOnlyList<PendingBatchRequest<TRequest>> pendingRequests,\n"
                + "        CancellationToken cancellationToken)\n"
                + "    {\n"
                + "        EvaluationOffloadEntered?.TrySetResult();\n"
                + "        if (EvaluationOffloadGate is { } gate)\n"
                + "        {\n"
                + "            await gate.Task.ConfigureAwait(false);\n"
                + "        }\n\n"
                + "        ValueTask processing;\n"
                + "        try\n"
                + "        {\n"
                + "            processing = _batchConsumer.ExecuteAsync(pendingRequests, cancellationToken);\n"
                + "        }\n"
                + "        catch (Exception exception)\n"
                + "        {\n"
                + "            if (Interlocked.CompareExchange(ref _executionFailure, exception, null) is null)\n"
                + "            {\n"
                + "                _executionFailureCancellation.Cancel();\n"
                + "            }\n\n"
                + "            CompleteExecution();\n"
                + "            _executionSlots.Release();\n"
                + "            return;\n"
                + "        }\n\n"
                + "        await ProcessBatchAsync(processing).ConfigureAwait(false);\n"
                + "    }\n\n"
                + "    private async Task ProcessBatchAsync(ValueTask processing)");

        var probeDirectory = Path.Combine(scratch, "Probe");
        Directory.CreateDirectory(probeDirectory);
        File.Copy(Path.Combine(repository, "scripts", "scheduling-evaluation", "Program.cs"),
            Path.Combine(probeDirectory, "Program.cs"));
        await File.WriteAllTextAsync(
            Path.Combine(probeDirectory, "Probe.csproj"),
            """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <AssemblyName>RequestBatcher.Benchmarks</AssemblyName>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../src/RequestBatcher/RequestBatcher.csproj" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="10.0.11" />
  </ItemGroup>
</Project>
""").ConfigureAwait(false);
        await RunCheckedAsync(
            scratch,
            new Dictionary<string, string?> { ["DOTNET_TieredCompilation"] = "0" },
            "dotnet",
            ["build", "Probe/Probe.csproj", "-c", "Release", "--nologo"]).ConfigureAwait(false);
    }

    private static List<object[]> CreateCases()
    {
        var cases = new List<object[]>();
        foreach (var kind in new[] { "fast", "io", "cpu", "blocking" })
        {
            foreach (var batch in new[] { 1, 64 })
            {
                var count = kind switch
                {
                    "fast" => 262144,
                    "io" or "blocking" => 512 * batch,
                    "cpu" => 2048 * batch,
                    _ => throw new InvalidOperationException(),
                };
                foreach (var offload in new[] { false, true })
                {
                    cases.Add(new object[] { kind, batch, 8, 1, offload, 8, "none", count });
                }
            }
        }

        foreach (var concurrency in new[] { 1, 64 })
        {
            foreach (var kind in new[] { "fast", "io", "blocking" })
            {
                foreach (var offload in new[] { false, true })
                {
                    cases.Add(new object[] { kind, 1, concurrency, 1, offload, 8, "none", kind == "fast" ? 262144 : 512 });
                }
            }
        }

        foreach (var (keys, producers, kind, partitions) in new[]
        {
            ("none", 1, "fast", 1),
            ("none", 8, "fast", 1),
            ("uniform", 8, "fast", 8),
            ("skew", 8, "fast", 8),
            ("none", 8, "io", 1),
            ("uniform", 8, "io", 8),
            ("skew", 8, "io", 8),
        })
        {
            var candidate = new object[] { kind, 64, 8, partitions, false, producers, keys, kind == "fast" ? 262144 : 32768 };
            if (!cases.Any(existing => existing.SequenceEqual(candidate)))
            {
                cases.Add(candidate);
            }
        }

        var random = new Random(7824);
        random.Shuffle(CollectionsMarshal.AsSpan(cases));
        return cases;
    }

    private static Options ParseOptions(string[] args)
    {
        string? output = null;
        var prepareOnly = false;
        var selfCheck = false;
        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--output" when index + 1 < args.Length:
                    output = args[++index];
                    break;
                case "--prepare-only":
                    prepareOnly = true;
                    break;
                case "--self-check":
                    selfCheck = true;
                    break;
                default:
                    throw new ArgumentException($"Unknown or incomplete argument: {args[index]}");
            }
        }

        return output is null
            ? throw new ArgumentException("--output is required.")
            : new Options(output, prepareOnly, selfCheck);
    }

    private static string RepositoryRoot([CallerFilePath] string scriptPath = "")
    {
        var directory = Directory.GetParent(Path.GetFullPath(scriptPath))?.Parent?.Parent?.FullName;
        return directory ?? throw new InvalidOperationException("Could not locate the repository root.");
    }

    private static void ReplaceOnce(string path, string before, string after)
    {
        var text = File.ReadAllText(path);
        var count = CountOccurrences(text, before);
        if (count != 1)
        {
            throw new InvalidOperationException($"Expected exactly one patch location in {path}; found {count}.");
        }

        File.WriteAllText(path, text.Replace(before, after, StringComparison.Ordinal));
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        for (var index = 0; (index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0; index += value.Length)
        {
            count++;
        }

        return count;
    }

    private static void CopyDirectory(string source, string destination, Func<string, bool> include)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            if (!include(file))
            {
                continue;
            }

            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    private static Task<ProcessResult> RunCheckedAsync(
        string workingDirectory,
        IReadOnlyDictionary<string, string?>? environment,
        string fileName,
        params string[] arguments)
        => RunCheckedAsync(workingDirectory, environment, fileName, (IReadOnlyList<string>)arguments, null);

    private static async Task<ProcessResult> RunCheckedAsync(
        string workingDirectory,
        IReadOnlyDictionary<string, string?>? environment,
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan? timeout)
    {
        var result = await RunProcessAsync(workingDirectory, environment, fileName, arguments, timeout).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Command failed with exit code {result.ExitCode}: {fileName} {string.Join(' ', arguments)}\n"
                + result.Stdout + result.Stderr);
        }

        return result;
    }

    private static async Task<string> RunTextAsync(string workingDirectory, string fileName, params string[] arguments)
    {
        var result = await RunCheckedAsync(workingDirectory, null, fileName, arguments).ConfigureAwait(false);
        return result.Stdout.Trim();
    }

    private static async Task<ProcessResult> RunProcessAsync(
        string workingDirectory,
        IReadOnlyDictionary<string, string?>? environment,
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan? timeout)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (environment is not null)
        {
            foreach (var pair in environment)
            {
                startInfo.Environment[pair.Key] = pair.Value;
            }
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Could not start {fileName}.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var cancellation = timeout is { } limit ? new CancellationTokenSource(limit) : null;
        try
        {
            await process.WaitForExitAsync(cancellation?.Token ?? CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation?.IsCancellationRequested == true)
        {
            TryKill(process);
            await process.WaitForExitAsync().ConfigureAwait(false);
            throw new TimeoutException($"Command timed out: {fileName} {string.Join(' ', arguments)}");
        }

        return new ProcessResult(process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
    }

    private static void TryKill(Process process)
    {
        if (!process.HasExited)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
        }
    }

    private static void PrintHelp(TextWriter? writer = null)
    {
        writer ??= Console.Out;
        writer.WriteLine("Usage: dotnet run --file scripts/scheduling-evaluation/EvaluateScheduling.cs -- --output PATH [options]");
        writer.WriteLine("Options:");
        writer.WriteLine("  --prepare-only  Build the isolated probe and write environment.json only.");
        writer.WriteLine("  --self-check    Run the bounded offload correctness check.");
        writer.WriteLine("  --help          Show this help.");
    }

    private sealed record Options(string Output, bool PrepareOnly, bool SelfCheck);

    private sealed record ProcessResult(int ExitCode, string Stdout, string Stderr);

    private sealed class JsonLinesWriter(string path) : IAsyncDisposable
    {
        private readonly StreamWriter _writer = new(path, append: false);

        public async ValueTask WriteAsync(string json)
        {
            await _writer.WriteLineAsync(json.TrimEnd()).ConfigureAwait(false);
            await _writer.FlushAsync().ConfigureAwait(false);
        }

        public ValueTask DisposeAsync() => _writer.DisposeAsync();
    }
}
