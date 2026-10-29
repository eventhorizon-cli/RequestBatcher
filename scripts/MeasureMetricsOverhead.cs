#:property TargetFramework=net10.0
#:property PublishAot=false
#:property LangVersion=12.0
#:project ../tests/RequestBatcher.Benchmarks/RequestBatcher.Benchmarks.csproj
#:project Common/RequestBatcher.ScriptUtilities.csproj

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using RequestBatcher.Benchmarks.InMemory;
using RequestBatcher.ScriptUtilities;

if (args is ["--help"])
{
    Console.WriteLine("MeasureMetricsOverhead <Disabled|AggregationOnly> <single|multi> <output.json> [waves=30]\nRuns five warmup waves, then measured waves of the existing 1,024-item, 20 ms metrics workload. Validates counts and exactly-once handling. Use Release and --no-cache to rebuild referenced source; durations are per wave, not caller latency. Run baseline and candidate in separate processes on .NET 10.");
    return;
}

if (args.Length is < 3 or > 4 ||
    !Enum.TryParse<MetricsIoOverheadBenchmarks.MetricsMode>(args[0], out var mode) ||
    mode is not (MetricsIoOverheadBenchmarks.MetricsMode.Disabled or MetricsIoOverheadBenchmarks.MetricsMode.AggregationOnly) ||
    args[1] is not ("single" or "multi") ||
    (args.Length == 4 && (!int.TryParse(args[3], out var number) || number is < 1 or > 1000)))
{
    throw new ArgumentException("Invalid arguments. Use --help.");
}

var waves = args.Length == 4 ? int.Parse(args[3]) : 30;
var workload = new MetricsIoOverheadBenchmarks { Mode = mode };
workload.GlobalSetup();
try
{
    Func<Task> run = args[1] == "single" ? workload.SingleSubmissionsAsync : workload.ExplicitGroupsAsync;
    for (var i = 0; i < 5; i++)
    {
        await run().ConfigureAwait(false);
    }

    var elapsed = new double[waves];
    var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
    for (var i = 0; i < waves; i++)
    {
        var start = Stopwatch.GetTimestamp();
        await run().ConfigureAwait(false);
        elapsed[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    }

    var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
    var output = Path.GetFullPath(args[2]);
    var result = new
    {
        Runtime = RuntimeInformation.FrameworkDescription,
        OS = RuntimeInformation.OSDescription,
        Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
        Mode = mode.ToString(),
        SubmissionKind = args[1],
        Waves = waves,
        RequestsPerWave = 1024,
        BatchSize = 32,
        Concurrency = 8,
        DownstreamDelayMilliseconds = 20,
        AllocatedBytes = allocated,
        BytesPerItem = allocated / (double)(waves * 1024),
        WaveMilliseconds = SampleSummary.Calculate(elapsed),
        Notes = "Bounded allocation/timing probe of the existing metrics benchmark; five warmups excluded. Metrics aggregation enabled only in AggregationOnly, no exporter in the measured region, no tracing. Not a per-call latency benchmark."
    };
    Directory.CreateDirectory(Path.GetDirectoryName(output)!);
    // Write results only after workload cleanup validates cumulative exported metrics and handling.
    await workload.GlobalCleanupAsync().ConfigureAwait(false);
    await File.WriteAllTextAsync(output, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true })).ConfigureAwait(false);
    Console.WriteLine(JsonSerializer.Serialize(result));
}
catch
{
    await workload.GlobalCleanupAsync().ConfigureAwait(false);
    throw;
}
