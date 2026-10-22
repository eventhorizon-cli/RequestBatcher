#:property TargetFramework=net8.0
#:property PublishAot=false
#:property LangVersion=12.0
#:package Microsoft.Diagnostics.Tracing.TraceEvent@3.1.21

using System.Globalization;
using System.Text;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;

internal static class AnalyzeAllocationTrace
{
    private const string MarkerProvider = "RequestBatcher-AllocationProbe";
    private const string RuntimeProvider = "Microsoft-Windows-DotNETRuntime";

    private static int Main(string[] args)
    {
        if (args is ["--help"])
        {
            Console.WriteLine(
                "AnalyzeAllocationTrace <trace.nettrace> <output-prefix>\n" +
                "Reads one EventPipe trace with one RequestBatcher-AllocationProbe marker window and writes " +
                "<output-prefix>.types.csv, <output-prefix>.stacks.csv, and <output-prefix>.summary.txt. " +
                "Allocation weights are sampled estimates, not object counts or exact byte totals.");
            return 0;
        }

        if (args.Length != 2 || args.Any(static argument => argument.Length > 0 && argument[0] == '-'))
        {
            Console.Error.WriteLine("Invalid arguments. Use --help.");
            return 2;
        }

        var tracePath = Path.GetFullPath(args[0]);
        var outputPrefix = Path.GetFullPath(args[1]);
        try
        {
            if (!File.Exists(tracePath))
            {
                throw new FileNotFoundException("Trace file was not found.", tracePath);
            }

            var outputDirectory = Path.GetDirectoryName(outputPrefix);
            if (string.IsNullOrEmpty(outputDirectory))
            {
                throw new InvalidOperationException("The output prefix must include a writable directory.");
            }

            Directory.CreateDirectory(outputDirectory);
            var etlxPath = outputPrefix + ".etlx";
            var result = Analyze(tracePath, etlxPath);
            WriteReports(outputPrefix, tracePath, result);
            File.Delete(etlxPath);
            Console.WriteLine($"Types: {outputPrefix}.types.csv");
            Console.WriteLine($"Stacks: {outputPrefix}.stacks.csv");
            Console.WriteLine($"Summary: {outputPrefix}.summary.txt");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    private static AnalysisResult Analyze(string tracePath, string etlxPath)
    {
        var conversionOptions = new TraceLogOptions
        {
            ContinueOnError = false,
            KeepAllEvents = true,
        };
        TraceLog.CreateFromEventPipeDataFile(tracePath, etlxPath, conversionOptions);
        using var trace = new TraceLog(etlxPath);

        Marker? start = null;
        Marker? stop = null;
        var startCount = 0;
        var stopCount = 0;

        foreach (var traceEvent in trace.Events)
        {
            if (!IsMarker(traceEvent))
            {
                continue;
            }

            if ((int)traceEvent.ID == 1 && traceEvent.Opcode == TraceEventOpcode.Start)
            {
                startCount++;
                start = new Marker(traceEvent.TimeStampRelativeMSec, ReadWaveCount(traceEvent));
            }
            else if ((int)traceEvent.ID == 2 && traceEvent.Opcode == TraceEventOpcode.Stop)
            {
                stopCount++;
                stop = new Marker(traceEvent.TimeStampRelativeMSec, ReadWaveCount(traceEvent));
            }
        }

        if (startCount != 1 || stopCount != 1 || start is null || stop is null || stop.TimeMsec < start.TimeMsec)
        {
            throw new InvalidOperationException(
                $"Expected exactly one ordered Start/Stop marker window; found starts={startCount}, stops={stopCount}.");
        }

        if (start.WaveCount != stop.WaveCount)
        {
            throw new InvalidOperationException(
                $"Start and Stop waveCount values differ: {start.WaveCount} and {stop.WaveCount}.");
        }

        var types = new Dictionary<string, Aggregate>(StringComparer.Ordinal);
        var stacks = new Dictionary<StackKey, Aggregate>();
        long allocationSampleCount = 0;
        long allocationWeight = 0;
        long noStackSampleCount = 0;
        long noStackWeight = 0;
        var firstAllocationMsec = double.PositiveInfinity;
        var lastAllocationMsec = double.NegativeInfinity;

        foreach (var traceEvent in trace.Events)
        {
            var timestamp = traceEvent.TimeStampRelativeMSec;
            if (timestamp < start.TimeMsec || timestamp > stop.TimeMsec || !IsAllocationTick(traceEvent))
            {
                continue;
            }

            var weight = ReadAllocationWeight(traceEvent);
            var typeName = ReadPayloadString(traceEvent, "TypeName") ?? "<unknown>";
            var stack = ResolveStack(trace, traceEvent);
            Add(types, typeName, weight);
            Add(stacks, new StackKey(typeName, stack), weight);
            allocationSampleCount++;
            allocationWeight = checked(allocationWeight + weight);
            if (stack == "<no-stack>")
            {
                noStackSampleCount++;
                noStackWeight = checked(noStackWeight + weight);
            }
            firstAllocationMsec = Math.Min(firstAllocationMsec, timestamp);
            lastAllocationMsec = Math.Max(lastAllocationMsec, timestamp);
        }

        if (allocationSampleCount == 0)
        {
            throw new InvalidOperationException("The marker window contained no GCAllocationTick samples.");
        }

        return new AnalysisResult(
            tracePath,
            start,
            stop,
            types,
            stacks,
            allocationSampleCount,
            allocationWeight,
            firstAllocationMsec,
            lastAllocationMsec,
            noStackSampleCount,
            noStackWeight,
            trace.EventsLost,
            trace.EventCount);
    }

    private static bool IsMarker(TraceEvent traceEvent) =>
        string.Equals(traceEvent.ProviderName, MarkerProvider, StringComparison.OrdinalIgnoreCase);

    private static bool IsAllocationTick(TraceEvent traceEvent) =>
        string.Equals(traceEvent.ProviderName, RuntimeProvider, StringComparison.OrdinalIgnoreCase) &&
        (traceEvent.EventName.Contains("AllocationTick", StringComparison.OrdinalIgnoreCase) ||
            traceEvent.TaskName.Contains("AllocationTick", StringComparison.OrdinalIgnoreCase)) &&
        FindPayloadIndex(traceEvent, "AllocationAmount64") >= 0;

    private static int ReadWaveCount(TraceEvent traceEvent)
    {
        var value = ReadPayload(traceEvent, "waveCount");
        if (value is null || !int.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out var waveCount) || waveCount <= 0)
        {
            throw new InvalidOperationException("Allocation marker waveCount must be a positive integer.");
        }

        return waveCount;
    }

    private static long ReadAllocationWeight(TraceEvent traceEvent)
    {
        var value = ReadPayload(traceEvent, "AllocationAmount64");
        try
        {
            var weight = Convert.ToInt64(value, CultureInfo.InvariantCulture);
            if (weight < 0)
            {
                throw new InvalidOperationException("GCAllocationTick AllocationAmount64 cannot be negative.");
            }

            return weight;
        }
        catch (Exception exception) when (exception is FormatException or InvalidCastException or OverflowException)
        {
            throw new InvalidOperationException("GCAllocationTick AllocationAmount64 was not an integer.", exception);
        }
    }

    private static object? ReadPayload(TraceEvent traceEvent, string name)
    {
        var index = FindPayloadIndex(traceEvent, name);
        return index < 0 ? null : traceEvent.PayloadValue(index);
    }

    private static string? ReadPayloadString(TraceEvent traceEvent, string name)
    {
        var value = ReadPayload(traceEvent, name);
        var text = Convert.ToString(value, CultureInfo.InvariantCulture);
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static int FindPayloadIndex(TraceEvent traceEvent, string name)
    {
        var names = traceEvent.PayloadNames;
        for (var index = 0; index < names.Length; index++)
        {
            if (string.Equals(names[index], name, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }

    private static string ResolveStack(TraceLog trace, TraceEvent traceEvent)
    {
        var stack = trace.GetCallStackForEvent(traceEvent);
        if (stack is null)
        {
            return "<no-stack>";
        }

        var frames = new List<string>();
        for (var frame = stack; frame is not null; frame = frame.Caller)
        {
            var name = frame.CodeAddress.FullMethodName;
            if (string.IsNullOrWhiteSpace(name))
            {
                name = frame.CodeAddress.ModuleName;
            }

            frames.Add(string.IsNullOrWhiteSpace(name) ? "<unknown-frame>" : name);
        }

        return string.Join(" <- ", frames);
    }

    private static void Add<TKey>(Dictionary<TKey, Aggregate> aggregates, TKey key, long weight)
        where TKey : notnull
    {
        if (aggregates.TryGetValue(key, out var aggregate))
        {
            aggregates[key] = aggregate with
            {
                Weight = checked(aggregate.Weight + weight),
                Samples = checked(aggregate.Samples + 1),
            };
        }
        else
        {
            aggregates.Add(key, new Aggregate(weight, 1));
        }
    }

    private static void WriteReports(string outputPrefix, string tracePath, AnalysisResult result)
    {
        WriteCsv(outputPrefix + ".types.csv", "type_name,allocation_weight_bytes,sample_count", result.Types);
        WriteCsv(outputPrefix + ".stacks.csv", "type_name,allocation_stack,allocation_weight_bytes,sample_count", result.Stacks);

        var summary = new StringBuilder()
            .AppendLine($"trace={tracePath}")
            .AppendLine("traceevent_package=Microsoft.Diagnostics.Tracing.TraceEvent 3.1.21")
            .AppendLine($"marker_provider={MarkerProvider}")
            .AppendLine($"start_time_msec={result.Start.TimeMsec.ToString("R", CultureInfo.InvariantCulture)}")
            .AppendLine($"stop_time_msec={result.Stop.TimeMsec.ToString("R", CultureInfo.InvariantCulture)}")
            .AppendLine($"duration_msec={(result.Stop.TimeMsec - result.Start.TimeMsec).ToString("R", CultureInfo.InvariantCulture)}")
            .AppendLine($"wave_count={result.Start.WaveCount}")
            .AppendLine($"allocation_samples={result.AllocationSamples}")
            .AppendLine($"allocation_weight_bytes={result.AllocationWeight}")
            .AppendLine($"allocation_type_count={result.Types.Count}")
            .AppendLine($"allocation_stack_count={result.Stacks.Count}")
            .AppendLine($"no_stack_samples={result.NoStackSamples}")
            .AppendLine($"no_stack_weight_bytes={result.NoStackWeight}")
            .AppendLine($"first_allocation_time_msec={result.FirstAllocationMsec.ToString("R", CultureInfo.InvariantCulture)}")
            .AppendLine($"last_allocation_time_msec={result.LastAllocationMsec.ToString("R", CultureInfo.InvariantCulture)}")
            .AppendLine($"events_lost={result.EventsLost}")
            .AppendLine($"events_in_etlx={result.EventCount}")
            .AppendLine("limitations=GCAllocationTick values are sampled weights; sample_count is not object_count; stack names depend on trace metadata and available symbols.")
            .ToString();
        File.WriteAllText(outputPrefix + ".summary.txt", summary);
    }

    private static void WriteCsv(string path, string header, Dictionary<string, Aggregate> values)
    {
        var builder = new StringBuilder().AppendLine(header);
        foreach (var pair in values.OrderByDescending(pair => pair.Value.Weight).ThenBy(pair => pair.Key, StringComparer.Ordinal))
        {
            builder.Append(Csv(pair.Key))
                .Append(',')
                .Append(pair.Value.Weight.ToString(CultureInfo.InvariantCulture))
                .Append(',')
                .AppendLine(pair.Value.Samples.ToString(CultureInfo.InvariantCulture));
        }

        File.WriteAllText(path, builder.ToString());
    }

    private static void WriteCsv(string path, string header, Dictionary<StackKey, Aggregate> values)
    {
        var builder = new StringBuilder().AppendLine(header);
        foreach (var pair in values
            .OrderByDescending(pair => pair.Value.Weight)
            .ThenBy(pair => pair.Key.TypeName, StringComparer.Ordinal)
            .ThenBy(pair => pair.Key.Stack, StringComparer.Ordinal))
        {
            builder.Append(Csv(pair.Key.TypeName))
                .Append(',')
                .Append(Csv(pair.Key.Stack))
                .Append(',')
                .Append(pair.Value.Weight.ToString(CultureInfo.InvariantCulture))
                .Append(',')
                .AppendLine(pair.Value.Samples.ToString(CultureInfo.InvariantCulture));
        }

        File.WriteAllText(path, builder.ToString());
    }

    private static string Csv(string value) =>
        $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private sealed record Marker(double TimeMsec, int WaveCount);

    private sealed record StackKey(string TypeName, string Stack);

    private sealed record Aggregate(long Weight, long Samples);

    private sealed record AnalysisResult(
        string TracePath,
        Marker Start,
        Marker Stop,
        Dictionary<string, Aggregate> Types,
        Dictionary<StackKey, Aggregate> Stacks,
        long AllocationSamples,
        long AllocationWeight,
        double FirstAllocationMsec,
        double LastAllocationMsec,
        long NoStackSamples,
        long NoStackWeight,
        long EventsLost,
        long EventCount);
}
