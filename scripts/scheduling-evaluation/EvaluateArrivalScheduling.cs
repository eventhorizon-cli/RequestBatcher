#:property TargetFramework=net10.0
#:property PublishAot=false
#:property LangVersion=12.0

using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

internal static class EvaluateArrivalScheduling
{
    private static readonly string[] _snapshotRoots =
    [
        "src/RequestBatcher",
        "src/RequestBatcher.Analyzers",
        "src/RequestBatcher.OpenTelemetry",
        "tests/RequestBatcher.Benchmarks",
    ];

    private static readonly string[] _snapshotFiles =
    [
        "global.json",
        "Directory.Build.props",
        ".editorconfig",
    ];

    private static async Task<int> Main(string[] args)
    {
        if (args.Any(argument => argument is "--help" or "-h"))
        {
            PrintUsage();
            return 0;
        }

        try
        {
            await RunAsync(Options.Parse(args)).ConfigureAwait(false);
            return 0;
        }
        catch (CommandLineException exception)
        {
            Console.Error.WriteLine($"error: {exception.Message}");
            PrintUsage();
            return 2;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static async Task RunAsync(Options options)
    {
        var repository = LocateRepository();
        var sourceManifest = SnapshotManifest(repository);
        var output = Path.GetFullPath(options.Output!);
        Directory.CreateDirectory(output);

        var scratch = PrepareCopy(repository);
        var scratchManifest = SnapshotManifest(scratch);
        var environment = CreateEnvironment();
        environment["DOTNET_TieredCompilation"] = "0";

        var project = Path.Combine(scratch, "tests/RequestBatcher.Benchmarks/RequestBatcher.Benchmarks.csproj");
        var build = await RunProcessAsync(
            "dotnet",
            ["build", project, "-c", "Release", "--nologo"],
            scratch,
            environment,
            TimeSpan.FromSeconds(options.TimeoutSeconds)).ConfigureAwait(false);
        EnsureSucceeded(build, "dotnet build");

        var sourceCommit = await ReadGitValueAsync(repository, ["rev-parse", "HEAD"]).ConfigureAwait(false);
        var workingTreeStatus = (await ReadGitValueAsync(repository, ["status", "--porcelain"]).ConfigureAwait(false))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .ToList();
        var metadata = new Dictionary<string, object?>
        {
            ["Scratch"] = scratch,
            ["SourceCommit"] = sourceCommit,
            ["SourceSnapshotSha256"] = ManifestHash(sourceManifest),
            ["ScratchSnapshotSha256"] = ManifestHash(scratchManifest),
            ["PrototypeDiffSha256"] = ManifestDiffHash(sourceManifest, scratchManifest),
            ["RunnerSha256"] = FileHash(GetScriptPath()),
            ["WorkingTreeDirty"] = workingTreeStatus.Count != 0,
            ["WorkingTreeStatus"] = workingTreeStatus,
            ["TieredCompilation"] = false,
            ["MaxConcurrency"] = 8,
            ["OnePartition"] = true,
            ["Database"] = options.Database,
            ["DatabaseOperation"] = options.DatabaseOperation,
            ["Isolation"] = "Copied core, OpenTelemetry, analyzer, and benchmark projects; hooks and derived core package references patched only in scratch",
        };
        WriteJson(Path.Combine(output, "environment.json"), metadata);
        Console.WriteLine($"Isolated source: {scratch}");

        try
        {
            if (!options.PrepareOnly)
            {
                var workloads = options.Database == "none" ? options.Workloads : [options.Database];
                var cases = CreateCases(options, workloads);
                var dll = Path.Combine(scratch, "tests/RequestBatcher.Benchmarks/bin/Release/net10.0/RequestBatcher.Benchmarks.dll");
                await RunCasesAsync(cases, dll, scratch, environment, output, options.TimeoutSeconds)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            if (!ManifestEquals(sourceManifest, SnapshotManifest(repository)))
            {
                throw new InvalidOperationException("The repository source changed during the isolated evaluation.");
            }
        }
    }

    private static async Task RunCasesAsync(
        IReadOnlyList<Dictionary<string, object?>> cases,
        string dll,
        string scratch,
        IReadOnlyDictionary<string, string> environment,
        string output,
        int timeoutSeconds)
    {
        await using var stream = new StreamWriter(Path.Combine(output, "measurements.jsonl"), false, new UTF8Encoding(false));
        for (var index = 0; index < cases.Count; index++)
        {
            var settings = cases[index];
            var settingsJson = JsonSerializer.Serialize(settings);
            var row = await RunCaseAsync(
                dll,
                settingsJson,
                scratch,
                environment,
                TimeSpan.FromSeconds(timeoutSeconds)).ConfigureAwait(false);
            await stream.WriteLineAsync(row).ConfigureAwait(false);
            await stream.FlushAsync().ConfigureAwait(false);

            using var document = JsonDocument.Parse(row);
            var throughput = ReadNumber(document.RootElement, "throughput", "Throughput");
            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"{index + 1}/{cases.Count} {settings["Workload"]} batch={settings["BatchSize"]} " +
                $"offload={settings["Offload"]} throughput={throughput:F0}/s"));
        }
    }

    private static async Task<string> RunCaseAsync(
        string dll,
        string settings,
        string scratch,
        IReadOnlyDictionary<string, string> environment,
        TimeSpan timeout)
    {
        var result = await RunProcessAsync(
            "dotnet",
            [dll, "--scheduling-probe", settings],
            scratch,
            environment,
            timeout).ConfigureAwait(false);
        EnsureSucceeded(result, $"scheduling probe {settings}");

        var lines = result.StandardOutput
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length != 1)
        {
            throw new InvalidOperationException($"Expected one JSON result for {settings}, got:\n{result.StandardOutput}");
        }

        using var document = JsonDocument.Parse(lines[0]);
        return JsonSerializer.Serialize(document.RootElement);
    }

    private static List<Dictionary<string, object?>> CreateCases(Options options, IReadOnlyList<string> workloads)
    {
        var cases = new List<Dictionary<string, object?>>();
        foreach (var workload in workloads)
        {
            foreach (var rate in options.Rates)
            {
                foreach (var batchSize in options.BatchSizes)
                {
                    foreach (var offload in new[] { false, true })
                    {
                        for (var repeat = 0; repeat < options.Repeats; repeat++)
                        {
                            cases.Add(new Dictionary<string, object?>
                            {
                                ["Workload"] = workload,
                                ["DatabaseOperation"] = options.DatabaseOperation,
                                ["IntendedRate"] = rate,
                                ["DurationMilliseconds"] = options.DurationMilliseconds,
                                ["MaxItems"] = options.MaxItems,
                                ["WarmupItems"] = options.WarmupItems,
                                ["BatchSize"] = batchSize,
                                ["MaxConcurrency"] = 8,
                                ["Offload"] = offload,
                            });
                        }
                    }
                }
            }
        }

        var random = new Random(5187);
        for (var index = cases.Count - 1; index > 0; index--)
        {
            var swap = random.Next(index + 1);
            (cases[index], cases[swap]) = (cases[swap], cases[index]);
        }

        return cases;
    }

    private static string PrepareCopy(string repository)
    {
        var scratch = Directory.CreateTempSubdirectory("requestbatcher-arrival-").FullName;
        foreach (var relativeFile in _snapshotFiles)
        {
            File.Copy(Path.Combine(repository, relativeFile), Path.Combine(scratch, relativeFile));
        }

        foreach (var relativeRoot in _snapshotRoots)
        {
            CopyDirectory(
                Path.Combine(repository, relativeRoot),
                Path.Combine(scratch, relativeRoot));
        }

        PatchEvaluationHooks(scratch);
        AddCorePackageReferencesToScratchBenchmark(scratch);
        return scratch;
    }

    private static void AddCorePackageReferencesToScratchBenchmark(string scratch)
    {
        // Keep the isolated benchmark's runtime dependency manifest aligned with the copied core project.
        // Derive these references from that project instead of maintaining a second set of package versions here.
        var coreProject = XDocument.Load(Path.Combine(scratch, "src/RequestBatcher/RequestBatcher.csproj"));
        var benchmarkPath = Path.Combine(scratch, "tests/RequestBatcher.Benchmarks/RequestBatcher.Benchmarks.csproj");
        var benchmarkProject = XDocument.Load(benchmarkPath);
        var packageReferences = benchmarkProject
            .Descendants("PackageReference")
            .Select(reference => (string?)reference.Attribute("Include"))
            .Where(static include => include is not null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var targetGroup = benchmarkProject.Root!.Elements("ItemGroup").FirstOrDefault()
            ?? new XElement("ItemGroup");
        if (targetGroup.Parent is null)
        {
            benchmarkProject.Root.Add(targetGroup);
        }

        foreach (var reference in coreProject.Descendants("PackageReference"))
        {
            var include = (string?)reference.Attribute("Include");
            var version = (string?)reference.Attribute("Version");
            if (include is null || version is null || !packageReferences.Add(include))
            {
                continue;
            }

            targetGroup.Add(new XElement(
                "PackageReference",
                new XAttribute("Include", include),
                new XAttribute("Version", version)));
        }

        benchmarkProject.Save(benchmarkPath);
    }

    private static void PatchEvaluationHooks(string scratch)
    {
        var loop = Path.Combine(scratch, "src/RequestBatcher/Scheduling/BatchDispatchLoop.cs");
        ReplaceOnce(
            loop,
            "    private readonly RequestBatchConsumer<TRequest> _batchConsumer;",
            """
    internal static bool EvaluationOffload;
    internal static Action? EvaluationExecutionStarted;
    internal static Action? EvaluationExecutionFinished;
    internal static Action<int>? EvaluationBatchStarted;
    private readonly RequestBatchConsumer<TRequest> _batchConsumer;
"""
        );
        ReplaceOnce(
            loop,
            """
        TrackExecution();
        var processing = _batchConsumer.ExecuteAsync(pendingRequests, cancellationToken);
""",
            """
        TrackExecution();
        if (EvaluationOffload)
        {
            _ = ProcessOffloadedBatchAsync(pendingRequests, cancellationToken);
            return;
        }

        var processing = _batchConsumer.ExecuteAsync(pendingRequests, cancellationToken);
"""
        );
        ReplaceOnce(
            loop,
            "    private async Task ProcessBatchAsync(ValueTask processing)",
            """
    private async Task ProcessOffloadedBatchAsync(
        IReadOnlyList<PendingBatchRequest<TRequest>> pendingRequests,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Run(
                async () => await _batchConsumer.ExecuteAsync(pendingRequests, cancellationToken)
                    .ConfigureAwait(false)).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            if (Interlocked.CompareExchange(ref _executionFailure, exception, null) is null)
            {
                _executionFailureCancellation.Cancel();
            }

        }
        finally
        {
            CompleteExecution();
            _executionSlots.Release();
        }
    }

    private async Task ProcessBatchAsync(ValueTask processing)
"""
        );
        ReplaceOnce(
            loop,
            """
            _inFlightCount++;
        }
    }

    private void CompleteExecution()
""",
            """
            _inFlightCount++;
        }

        EvaluationExecutionStarted?.Invoke();
    }

    private void CompleteExecution()
"""
        );
        ReplaceOnce(
            loop,
            """
        drained?.TrySetResult();
    }

    private static TaskCompletionSource CreateCompletedSource()
""",
            """
        drained?.TrySetResult();
        EvaluationExecutionFinished?.Invoke();
    }

    private static TaskCompletionSource CreateCompletedSource()
"""
        );

        var consumer = Path.Combine(scratch, "src/RequestBatcher/Scheduling/RequestBatchConsumer.cs");
        ReplaceOnce(
            consumer,
            """
            logger.BatchStarted(requestTypeName, requests.Length);
            await handler(requests, cancellationToken).ConfigureAwait(false);
""",
            """
            logger.BatchStarted(requestTypeName, requests.Length);
            BatchDispatchLoop<TRequest>.EvaluationBatchStarted?.Invoke(requests.Length);
            await handler(requests, cancellationToken).ConfigureAwait(false);
"""
        );
    }

    private static void ReplaceOnce(string path, string before, string after)
    {
        var text = File.ReadAllText(path);
        var first = text.IndexOf(before, StringComparison.Ordinal);
        var count = 0;
        for (var index = first; index >= 0;)
        {
            count++;
            index = text.IndexOf(before, index + before.Length, StringComparison.Ordinal);
        }

        if (count != 1)
        {
            throw new InvalidOperationException($"Expected exactly one patch location in {path}; found {count}.");
        }

        File.WriteAllText(path, text.Replace(before, after, StringComparison.Ordinal));
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
            if (name is "bin" or "obj")
            {
                continue;
            }

            CopyDirectory(directory, Path.Combine(destination, name));
        }
    }

    private static Dictionary<string, string> SnapshotManifest(string root)
    {
        var manifest = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var relativeFile in _snapshotFiles)
        {
            var path = Path.Combine(root, relativeFile);
            if (File.Exists(path))
            {
                manifest[relativeFile] = FileHash(path);
            }
        }

        foreach (var relativeRoot in _snapshotRoots)
        {
            var path = Path.Combine(root, relativeRoot);
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                var parts = Path.GetRelativePath(root, file).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (parts.Any(part => part is "bin" or "obj"))
                {
                    continue;
                }

                var relative = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');
                manifest[relative] = FileHash(file);
            }
        }

        return manifest;
    }

    private static string ManifestHash(IReadOnlyDictionary<string, string> manifest)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var entry in manifest.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            AppendUtf8(hash, entry.Key);
            hash.AppendData([0]);
            AppendUtf8(hash, entry.Value);
            hash.AppendData([(byte)'\n']);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static string ManifestDiffHash(
        IReadOnlyDictionary<string, string> before,
        IReadOnlyDictionary<string, string> after)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var relative in before.Keys.Concat(after.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            before.TryGetValue(relative, out var oldHash);
            after.TryGetValue(relative, out var newHash);
            oldHash ??= "";
            newHash ??= "";
            if (oldHash == newHash)
            {
                continue;
            }

            AppendUtf8(hash, relative);
            hash.AppendData([0]);
            AppendUtf8(hash, oldHash);
            hash.AppendData([0]);
            AppendUtf8(hash, newHash);
            hash.AppendData([(byte)'\n']);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static bool ManifestEquals(
        IReadOnlyDictionary<string, string> first,
        IReadOnlyDictionary<string, string> second) =>
        first.Count == second.Count && first.All(entry => second.TryGetValue(entry.Key, out var value) && value == entry.Value);

    private static void AppendUtf8(IncrementalHash hash, string value) =>
        hash.AppendData(Encoding.UTF8.GetBytes(value));

    private static string FileHash(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private static Dictionary<string, string> CreateEnvironment()
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            environment[(string)entry.Key] = (string?)entry.Value ?? "";
        }

        return environment;
    }

    private static async Task<string> ReadGitValueAsync(string repository, IReadOnlyList<string> arguments)
    {
        var result = await RunProcessAsync("git", arguments, repository, null, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        EnsureSucceeded(result, $"git {string.Join(' ', arguments)}");
        return result.StandardOutput.TrimEnd('\r', '\n');
    }

    private static async Task<ProcessResult> RunProcessAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string>? environment,
        TimeSpan timeout)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (environment is not null)
        {
            foreach (var entry in environment)
            {
                startInfo.Environment[entry.Key] = entry.Value;
            }
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException($"Could not start {fileName}.");
        }

        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        var wait = process.WaitForExitAsync();
        if (await Task.WhenAny(wait, Task.Delay(timeout)).ConfigureAwait(false) != wait)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
            }

            await process.WaitForExitAsync().ConfigureAwait(false);
            var timedOutStdout = await stdout.ConfigureAwait(false);
            var timedOutStderr = await stderr.ConfigureAwait(false);
            throw new TimeoutException(
                $"{fileName} timed out after {timeout.TotalSeconds:F0} seconds.\n{timedOutStdout}\n{timedOutStderr}");
        }

        await wait.ConfigureAwait(false);
        return new ProcessResult(process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
    }

    private static void EnsureSucceeded(ProcessResult result, string operation)
    {
        if (result.ExitCode == 0)
        {
            return;
        }

        throw new InvalidOperationException(
            $"{operation} failed with exit code {result.ExitCode}.\n{result.StandardOutput}\n{result.StandardError}");
    }

    private static double ReadNumber(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var value) && value.TryGetDouble(out var number))
            {
                return number;
            }
        }

        return 0;
    }

    private static void WriteJson(string path, object value) =>
        File.WriteAllText(
            path,
            JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }) + "\n",
            new UTF8Encoding(false));

    private static string LocateRepository([CallerFilePath] string sourceFile = "")
    {
        var scriptDirectory = new DirectoryInfo(Path.GetDirectoryName(sourceFile)!);
        return scriptDirectory.Parent!.Parent!.FullName;
    }

    private static string GetScriptPath([CallerFilePath] string sourceFile = "") => sourceFile;

    private static void PrintUsage() => Console.WriteLine("""
Usage: dotnet run --file scripts/scheduling-evaluation/EvaluateArrivalScheduling.cs -- [options]

Required:
  --output PATH                  Artifact directory.

Options:
  --prepare-only                Build and retain the isolated source copy only.
  --smoke                       Run one fast case at rate 1000 and batch size 1.
  --workloads NAME [...]        Workloads (default: fast io cpu blocking).
  --rates NUMBER [...]          Arrival rates (default: 1000 10000).
  --batch-sizes NUMBER [...]    Batch sizes (default: 1 64).
  --duration-ms NUMBER          Arrival duration (default: 750).
  --max-items NUMBER            Maximum measured items (default: 20000).
  --warmup-items NUMBER         Warmup items (default: 256).
  --repeats NUMBER              Repetitions per case (default: 1).
  --database none|postgres|redis
                                Database workload (default: none).
  --database-operation write|read
                                Database operation (default: write).
  --timeout-seconds NUMBER      Child-process timeout (default: 180).
  --help                        Show this help.
""");

    private static CommandLineException Invalid(string message) => new(message);

    private sealed class Options
    {
        public string? Output { get; private set; }
        public bool PrepareOnly { get; private set; }
        public bool Smoke { get; private set; }
        public List<string> Workloads { get; private set; } = ["fast", "io", "cpu", "blocking"];
        public List<double> Rates { get; private set; } = [1_000, 10_000];
        public List<int> BatchSizes { get; private set; } = [1, 64];
        public int DurationMilliseconds { get; private set; } = 750;
        public int MaxItems { get; private set; } = 20_000;
        public int WarmupItems { get; private set; } = 256;
        public int Repeats { get; private set; } = 1;
        public string Database { get; private set; } = "none";
        public string DatabaseOperation { get; private set; } = "write";
        public int TimeoutSeconds { get; private set; } = 180;

        public static Options Parse(string[] args)
        {
            var options = new Options();
            for (var index = 0; index < args.Length; index++)
            {
                var token = args[index];
                var equals = token.IndexOf('=');
                var name = equals >= 0 ? token[..equals] : token;
                var inlineValue = equals >= 0 ? token[(equals + 1)..] : null;
                switch (name)
                {
                    case "--output":
                        options.Output = ReadSingle(args, ref index, inlineValue, name);
                        break;
                    case "--prepare-only":
                        EnsureNoValue(inlineValue, name);
                        options.PrepareOnly = true;
                        break;
                    case "--smoke":
                        EnsureNoValue(inlineValue, name);
                        options.Smoke = true;
                        break;
                    case "--workloads":
                        options.Workloads = ReadMany(args, ref index, inlineValue, name);
                        break;
                    case "--rates":
                        options.Rates = ReadMany(args, ref index, inlineValue, name)
                            .Select(value => ParseDouble(value, name)).ToList();
                        break;
                    case "--batch-sizes":
                        options.BatchSizes = ReadMany(args, ref index, inlineValue, name)
                            .Select(value => ParseInt(value, name)).ToList();
                        break;
                    case "--duration-ms":
                        options.DurationMilliseconds = ParseInt(ReadSingle(args, ref index, inlineValue, name), name);
                        break;
                    case "--max-items":
                        options.MaxItems = ParseInt(ReadSingle(args, ref index, inlineValue, name), name);
                        break;
                    case "--warmup-items":
                        options.WarmupItems = ParseInt(ReadSingle(args, ref index, inlineValue, name), name);
                        break;
                    case "--repeats":
                        options.Repeats = ParseInt(ReadSingle(args, ref index, inlineValue, name), name);
                        break;
                    case "--database":
                        options.Database = ReadSingle(args, ref index, inlineValue, name);
                        break;
                    case "--database-operation":
                        options.DatabaseOperation = ReadSingle(args, ref index, inlineValue, name);
                        break;
                    case "--timeout-seconds":
                        options.TimeoutSeconds = ParseInt(ReadSingle(args, ref index, inlineValue, name), name);
                        break;
                    default:
                        throw Invalid($"Unknown option '{token}'.");
                }
            }

            if (string.IsNullOrWhiteSpace(options.Output))
            {
                throw Invalid("--output is required.");
            }

            if (options.Rates.Any(rate => rate <= 0))
            {
                throw Invalid("--rates must be positive.");
            }

            if (options.BatchSizes.Any(batch => batch <= 0))
            {
                throw Invalid("--batch-sizes must be positive.");
            }

            if (options.DurationMilliseconds < 0 || options.MaxItems <= 0 || options.WarmupItems < 0)
            {
                throw Invalid("duration, max-items, and warmup-items are invalid.");
            }

            if (options.Repeats <= 0)
            {
                throw Invalid("--repeats must be positive.");
            }

            if (options.TimeoutSeconds <= 0)
            {
                throw Invalid("--timeout-seconds must be positive.");
            }

            if (options.Database is not ("none" or "postgres" or "redis"))
            {
                throw Invalid("--database must be none, postgres, or redis.");
            }

            if (options.DatabaseOperation is not ("write" or "read"))
            {
                throw Invalid("--database-operation must be write or read.");
            }

            if (options.Smoke)
            {
                options.Workloads = ["fast"];
                options.Rates = [1_000];
                options.BatchSizes = [1];
                options.DurationMilliseconds = 0;
                options.MaxItems = 128;
                options.WarmupItems = 16;
                options.Repeats = 1;
                options.Database = "none";
            }

            return options;
        }

        private static string ReadSingle(string[] args, ref int index, string? inlineValue, string name)
        {
            if (inlineValue is not null)
            {
                return inlineValue;
            }

            if (++index >= args.Length || args[index].StartsWith("--", StringComparison.Ordinal))
            {
                throw Invalid($"{name} requires a value.");
            }

            return args[index];
        }

        private static List<string> ReadMany(string[] args, ref int index, string? inlineValue, string name)
        {
            var values = new List<string>();
            if (!string.IsNullOrEmpty(inlineValue))
            {
                values.Add(inlineValue);
            }

            while (index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                values.Add(args[++index]);
            }

            if (values.Count == 0)
            {
                throw Invalid($"{name} requires at least one value.");
            }

            return values;
        }

        private static void EnsureNoValue(string? value, string name)
        {
            if (value is not null)
            {
                throw Invalid($"{name} does not take a value.");
            }
        }

        private static int ParseInt(string value, string name) =>
            int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)
                ? result
                : throw Invalid($"{name} requires an integer, got '{value}'.");

        private static double ParseDouble(string value, string name) =>
            double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result)
                ? result
                : throw Invalid($"{name} requires a number, got '{value}'.");
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

    private sealed class CommandLineException(string message) : Exception(message);
}
