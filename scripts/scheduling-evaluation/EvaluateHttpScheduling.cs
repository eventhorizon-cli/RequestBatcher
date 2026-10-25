#:property TargetFramework=net10.0
#:property PublishAot=false
#:property LangVersion=12.0

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;

internal static class EvaluateHttpScheduling
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
            var output = Path.GetFullPath(options.Output);
            Directory.CreateDirectory(output);
            var scriptDirectory = Directory.GetParent(ScriptPath())!.FullName;
            var prepare = await RunCheckedAsync(
                repository,
                null,
                "dotnet",
                [
                    "run", "--file", Path.Combine(scriptDirectory, "EvaluateScheduling.cs"), "--",
                    "--output", output, "--prepare-only",
                ]).ConfigureAwait(false);
            Console.Write(prepare.Stdout);

            var environmentPath = Path.Combine(output, "environment.json");
            var environment = JsonNode.Parse(await File.ReadAllTextAsync(environmentPath).ConfigureAwait(false))!.AsObject();
            var scratch = environment["Scratch"]?.GetValue<string>()
                ?? throw new InvalidOperationException("The scheduling preparation did not report a scratch directory.");
            var probeDirectory = Path.Combine(scratch, "Probe");
            File.Copy(Path.Combine(repository, "scripts", "scheduling-evaluation", "IoProgram.cs"),
                Path.Combine(probeDirectory, "Program.cs"), overwrite: true);
            var serverDirectory = Path.Combine(scratch, "Server");
            Directory.CreateDirectory(serverDirectory);
            File.Copy(Path.Combine(repository, "scripts", "scheduling-evaluation", "IoServer.cs"),
                Path.Combine(serverDirectory, "Program.cs"), overwrite: true);
            await File.WriteAllTextAsync(Path.Combine(serverDirectory, "Server.csproj"),
                """
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
</Project>
""").ConfigureAwait(false);

            var processEnvironment = new Dictionary<string, string?> { ["DOTNET_TieredCompilation"] = "0" };
            foreach (var project in new[] { "Probe/Probe.csproj", "Server/Server.csproj" })
            {
                await RunCheckedAsync(scratch, processEnvironment, "dotnet",
                    ["build", project, "-c", "Release", "--nologo", "-t:Rebuild"]).ConfigureAwait(false);
            }

            var url = $"http://127.0.0.1:{ReservePort()}/";
            var cases = CreateCases(options);
            var batchSubmission = options.BatchControl || options.LatencySweep;
            environment["Launches"] = options.Launches;
            environment["Cases"] = cases.Count;
            environment["BatchSize"] = 32;
            environment["Partitions"] = 1;
            environment["Window"] = 1024;
            environment["MeasuredRequests"] = options.MeasuredRequests;
            environment["WarmupRequests"] = options.WarmupRequests;
            environment["Iterations"] = options.Iterations;
            environment["LatencySweep"] = options.LatencySweep;
            environment["DelaysMs"] = options.LatencySweep
                ? new JsonArray(options.Delays.Select(delay => JsonValue.Create(delay)).ToArray())
                : new JsonArray();
            environment["Server"] = "Separate Kestrel process; loopback HTTP/1.1";
            environment["ThreadPoolMinimums"] = "Unchanged";
            environment["QueueSampleIntervalMs"] = 10;
            environment["BatchSubmission"] = batchSubmission;
            await File.WriteAllTextAsync(
                environmentPath,
                environment.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n").ConfigureAwait(false);

            var serverPath = Path.Combine(scratch, "Server", "bin", "Release", "net10.0", "Server.dll");
            using var server = StartProcess(scratch, processEnvironment, "dotnet",
                [serverPath, "--urls", url], Path.Combine(output, "server.log"));
            try
            {
                await WaitForServerAsync(server.Process, url).ConfigureAwait(false);
                var probes = new List<Probe>();
                for (var launch = 0; launch < options.Launches; launch++)
                {
                    foreach (var testCase in cases)
                    {
                        foreach (var offload in new[] { false, true })
                        {
                            probes.Add(new Probe(launch, new ProbeSettings(
                                testCase.Kind, testCase.DelayMs, testCase.Concurrency, testCase.Connections,
                                testCase.CpuIterations, offload, batchSubmission, options.MeasuredRequests,
                                options.WarmupRequests, options.Iterations)));
                        }
                    }
                }

                new Random(3901).Shuffle(CollectionsMarshal.AsSpan(probes));
                await using var measurements = new JsonLinesWriter(Path.Combine(output, "measurements.jsonl"));
                var probePath = Path.Combine(scratch, "Probe", "bin", "Release", "net10.0", "RequestBatcher.Benchmarks.dll");
                for (var index = 0; index < probes.Count; index++)
                {
                    var probe = probes[index];
                    var result = await RunCheckedAsync(scratch, processEnvironment, "dotnet",
                        [probePath, JsonSerializer.Serialize(probe.Settings), url],
                        TimeSpan.FromSeconds(180)).ConfigureAwait(false);
                    var row = JsonNode.Parse(result.Stdout)!.AsObject();
                    row["Launch"] = probe.Launch;
                    await measurements.WriteAsync(row.ToJsonString()).ConfigureAwait(false);
                    Console.WriteLine($"{index + 1}/{probes.Count} launch={probe.Launch} {row["Settings"]}");
                }
            }
            finally
            {
                server.Kill();
                await server.WaitForExitAsync().ConfigureAwait(false);
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

    private static List<TestCase> CreateCases(Options options)
    {
        if (options.Smoke)
        {
            return [new TestCase("async", 1, 1, 1, 0)];
        }

        if (options.LatencySweep)
        {
            return options.Delays.SelectMany(delay => new[]
            {
                new TestCase("async", delay, 8, 8, 0),
                new TestCase("async", delay, 32, 32, 0),
                new TestCase("async", delay, 32, 8, 0),
                new TestCase("prefix", delay, 32, 32, 2_000_000),
            }).ToList();
        }

        var cases = new List<TestCase>
        {
            new("async", 1, 8, 8, 0),
            new("async", 10, 8, 8, 0),
            new("async", 50, 8, 8, 0),
            new("async", 10, 32, 32, 0),
            new("async", 10, 32, 8, 0),
            new("prefix", 10, 8, 8, 250_000),
            new("prefix", 10, 8, 8, 2_000_000),
            new("prefix", 10, 32, 32, 2_000_000),
            new("suffix", 10, 8, 8, 2_000_000),
            new("suffix", 10, 32, 32, 2_000_000),
            new("blocking", 10, 8, 8, 0),
            new("blocking", 10, 32, 32, 0),
        };
        if (options.BatchControl)
        {
            cases.RemoveAll(testCase => testCase.Kind != "async");
        }

        return cases;
    }

    private static Options ParseOptions(string[] args)
    {
        string? output = null;
        var launches = 3;
        var batchControl = false;
        var latencySweep = false;
        var smoke = false;
        var delays = new List<int> { 10, 100, 500 };
        var measuredRequests = 8192;
        var warmupRequests = 4096;
        var iterations = 3;
        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--output" when index + 1 < args.Length:
                    output = args[++index];
                    break;
                case "--launches" when index + 1 < args.Length && int.TryParse(args[++index], out launches):
                    break;
                case "--batch-control":
                    batchControl = true;
                    break;
                case "--latency-sweep":
                    latencySweep = true;
                    break;
                case "--smoke":
                    smoke = true;
                    break;
                case "--delays":
                    delays.Clear();
                    while (index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal))
                    {
                        if (!int.TryParse(args[++index], out var delay))
                        {
                            throw new ArgumentException("--delays must contain integers.");
                        }
                        delays.Add(delay);
                    }
                    break;
                case "--measured-requests" when index + 1 < args.Length && int.TryParse(args[++index], out measuredRequests):
                    break;
                case "--warmup-requests" when index + 1 < args.Length && int.TryParse(args[++index], out warmupRequests):
                    break;
                case "--iterations" when index + 1 < args.Length && int.TryParse(args[++index], out iterations):
                    break;
                default:
                    throw new ArgumentException($"Unknown or incomplete argument: {args[index]}");
            }
        }

        if (output is null)
        {
            throw new ArgumentException("--output is required.");
        }
        if (launches < 1 || measuredRequests < 1 || measuredRequests % 1024 != 0
            || warmupRequests < 1 || warmupRequests % 1024 != 0 || iterations < 1)
        {
            throw new ArgumentException("launches, iterations, and request counts must be positive; request counts must be multiples of 1024.");
        }
        if (smoke && (batchControl || latencySweep))
        {
            throw new ArgumentException("--smoke cannot be combined with --batch-control or --latency-sweep.");
        }
        if (latencySweep && delays.Any(delay => delay < 0))
        {
            throw new ArgumentException("--delays must contain only non-negative integers.");
        }
        if (smoke)
        {
            launches = 1;
            measuredRequests = warmupRequests = 1024;
            iterations = 1;
        }

        return new Options(output, launches, batchControl, latencySweep, smoke, delays,
            measuredRequests, warmupRequests, iterations);
    }

    private static async Task WaitForServerAsync(Process process, string url)
    {
        using var client = new HttpClient { BaseAddress = new Uri(url), Timeout = TimeSpan.FromMilliseconds(500) };
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (process.HasExited)
            {
                throw new InvalidOperationException("HTTP server exited before becoming ready; inspect server.log.");
            }
            try
            {
                using var response = await client.GetAsync("stats").ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
            }
            catch (TaskCanceledException)
            {
            }
            await Task.Delay(100).ConfigureAwait(false);
        }

        throw new TimeoutException("HTTP server did not become ready.");
    }

    private static int ReservePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static string RepositoryRoot([CallerFilePath] string scriptPath = "")
    {
        var directory = Directory.GetParent(Path.GetFullPath(scriptPath))?.Parent?.Parent?.FullName;
        return directory ?? throw new InvalidOperationException("Could not locate the repository root.");
    }

    private static string ScriptPath([CallerFilePath] string scriptPath = "") => scriptPath;

    private static async Task<ProcessResult> RunCheckedAsync(
        string workingDirectory,
        IReadOnlyDictionary<string, string?>? environment,
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan? timeout = null)
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

    private static async Task<ProcessResult> RunProcessAsync(
        string workingDirectory,
        IReadOnlyDictionary<string, string?>? environment,
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan? timeout)
    {
        using var process = StartProcess(workingDirectory, environment, fileName, arguments);
        var stdout = process.Process.StandardOutput.ReadToEndAsync();
        var stderr = process.Process.StandardError.ReadToEndAsync();
        using var cancellation = timeout is { } limit ? new CancellationTokenSource(limit) : null;
        try
        {
            await process.Process.WaitForExitAsync(cancellation?.Token ?? CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation?.IsCancellationRequested == true)
        {
            process.Kill();
            await process.Process.WaitForExitAsync().ConfigureAwait(false);
            throw new TimeoutException($"Command timed out: {fileName} {string.Join(' ', arguments)}");
        }

        return new ProcessResult(process.Process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
    }

    private static RunningProcess StartProcess(
        string workingDirectory,
        IReadOnlyDictionary<string, string?>? environment,
        string fileName,
        IReadOnlyList<string> arguments,
        string? logPath = null)
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

        var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Could not start {fileName}.");
        var sink = logPath is null ? null : new LogSink(logPath);
        var output = sink is null ? null : DrainToFileAsync(process.StandardOutput, sink);
        var error = sink is null ? null : DrainToFileAsync(process.StandardError, sink);
        return new RunningProcess(process, output, error, sink);
    }

    private static async Task DrainToFileAsync(StreamReader reader, LogSink sink)
    {
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            await sink.WriteLineAsync(line).ConfigureAwait(false);
        }
    }

    private static void PrintHelp(TextWriter? writer = null)
    {
        writer ??= Console.Out;
        writer.WriteLine("Usage: dotnet run --file scripts/scheduling-evaluation/EvaluateHttpScheduling.cs -- --output PATH [options]");
        writer.WriteLine("Options:");
        writer.WriteLine("  --launches N             Number of launches (default 3).");
        writer.WriteLine("  --batch-control          Keep only async cases and submit bounded batches.");
        writer.WriteLine("  --latency-sweep          Run the delay sweep cases.");
        writer.WriteLine("  --delays N ...            Delay values for --latency-sweep (default 10 100 500).");
        writer.WriteLine("  --measured-requests N     Measured count, multiple of 1024 (default 8192).");
        writer.WriteLine("  --warmup-requests N       Warmup count, multiple of 1024 (default 4096).");
        writer.WriteLine("  --iterations N            Iterations per probe (default 3).");
        writer.WriteLine("  --smoke                   Run one small direct/offload loopback case.");
        writer.WriteLine("  --help                   Show this help.");
    }

    private sealed record Options(
        string Output, int Launches, bool BatchControl, bool LatencySweep, bool Smoke,
        IReadOnlyList<int> Delays, int MeasuredRequests, int WarmupRequests, int Iterations);

    private sealed record TestCase(string Kind, int DelayMs, int Concurrency, int Connections, int CpuIterations);

    private sealed record Probe(int Launch, ProbeSettings Settings);

    private sealed record ProbeSettings(
        string Kind, int DelayMs, int Concurrency, int Connections, int CpuIterations,
        bool Offload, bool BatchSubmission, int MeasuredRequests, int WarmupRequests, int Iterations);

    private sealed record ProcessResult(int ExitCode, string Stdout, string Stderr);

    private sealed class RunningProcess(Process process, Task? output, Task? error, LogSink? sink) : IDisposable
    {
        public Process Process { get; } = process;

        public void Kill()
        {
            if (!Process.HasExited)
            {
                try
                {
                    Process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }
            }
        }

        public async Task WaitForExitAsync()
        {
            await Process.WaitForExitAsync().ConfigureAwait(false);
            if (output is not null)
            {
                await output.ConfigureAwait(false);
            }
            if (error is not null)
            {
                await error.ConfigureAwait(false);
            }
        }

        public void Dispose()
        {
            sink?.Dispose();
            Process.Dispose();
        }
    }

    private sealed class LogSink : IDisposable
    {
        private readonly StreamWriter _writer;
        private readonly SemaphoreSlim _gate = new(1, 1);

        public LogSink(string path)
        {
            _writer = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite));
        }

        public async Task WriteLineAsync(string line)
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                await _writer.WriteLineAsync(line).ConfigureAwait(false);
                await _writer.FlushAsync().ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        public void Dispose()
        {
            _writer.Dispose();
            _gate.Dispose();
        }
    }

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
