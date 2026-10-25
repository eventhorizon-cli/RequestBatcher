#:property TargetFramework=net10.0
#:property LangVersion=12.0
#:property PublishAot=false

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.VisualBasic.FileIO;

internal static class SummarizePartitionSubmissions
{
    private const int ExpectedCaseCount = 6;
    private const int ExpectedDiagnosticRecordsPerCase = 2;
    private const int ExpectedDiagnosticRecordCount = ExpectedCaseCount * ExpectedDiagnosticRecordsPerCase;
    private const int BatchSize = 64;
    private const int MaxConcurrency = 4;
    private static readonly string[] Methods = ["SingleSubmissions", "ExplicitSubmissions"];
    private static readonly string[] Workloads = ["SmallBurst", "Backlog", "RepeatedKeys"];
    private static readonly int[] PartitionCounts = [1, 4];
    private static readonly Regex MeasurementPattern = new(
        "^\\s*(?<value>[+-]?(?:(?:[\\d,]+(?:\\.\\d*)?)|(?:\\.\\d+))(?:[eE][+-]?\\d+)?)\\s*(?<unit>KB|MB|ns|us|µs|μs|ms|s|B)?\\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static int Main(string[] args)
    {
        if (args is ["--help"])
        {
            Console.WriteLine("Usage: dotnet run --file scripts/SummarizePartitionSubmissions.cs -- <benchmark-report.csv> <benchmark.log> <output-directory>");
            Console.WriteLine("Reads one partition-submission API experiment (6 cases, two diagnostic records per case) and writes summary.csv and diagnostics.jsonl.");
            return 0;
        }

        if (args.Length != 3)
        {
            Console.Error.WriteLine("Expected <benchmark-report.csv> <benchmark.log> <output-directory>. Use --help for usage.");
            return 2;
        }

        try
        {
            var reportRows = ReadReportRows(args[0]);
            var diagnostics = ReadDiagnostics(args[1]);
            var summaries = BuildSummaries(reportRows, diagnostics);
            var outputDirectory = Path.GetFullPath(args[2]);
            Directory.CreateDirectory(outputDirectory);
            WriteSummary(Path.Combine(outputDirectory, "summary.csv"), summaries);
            WriteDiagnostics(Path.Combine(outputDirectory, "diagnostics.jsonl"), diagnostics);

            var waves = diagnostics.Sum(static item => item.WaveCount);
            var requests = diagnostics.Sum(static item => item.ProcessedRequests);
            Console.WriteLine(
                $"Validated {summaries.Count} cases, {diagnostics.Count} diagnostic records, {waves} waves, and {requests} requests.");
            Console.WriteLine($"Wrote {Path.Combine(outputDirectory, "summary.csv")}");
            Console.WriteLine($"Wrote {Path.Combine(outputDirectory, "diagnostics.jsonl")}");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Partition submission summary failed: {exception.Message}");
            return 1;
        }
    }

    private static List<ReportRow> ReadReportRows(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Benchmark report was not found.", path);
        }

        using var parser = new TextFieldParser(path)
        {
            Delimiters = [","],
            HasFieldsEnclosedInQuotes = true,
            TrimWhiteSpace = false,
        };
        if (parser.EndOfData)
        {
            throw new InvalidDataException("Benchmark report is empty.");
        }

        var header = parser.ReadFields() ?? throw new InvalidDataException("Benchmark report has no header.");
        var columns = header.Select((name, index) => (Name: name.Trim(), Index: index)).ToDictionary(
            static item => item.Name,
            static item => item.Index,
            StringComparer.OrdinalIgnoreCase);
        var allocatedColumn = columns.FirstOrDefault(static item =>
            item.Key.StartsWith("Allocated", StringComparison.OrdinalIgnoreCase));
        var requiredColumns = new[] { "Method", "Workload", "Partitions", "Mean", "Error" };
        foreach (var required in requiredColumns)
        {
            if (!columns.ContainsKey(required))
            {
                throw new InvalidDataException($"Benchmark report is missing the '{required}' column.");
            }
        }

        if (allocatedColumn.Key is null)
        {
            throw new InvalidDataException("Benchmark report is missing an Allocated Memory column.");
        }

        var rows = new List<ReportRow>();
        while (!parser.EndOfData)
        {
            var fields = parser.ReadFields();
            if (fields is null || fields.All(static field => string.IsNullOrWhiteSpace(field)))
            {
                continue;
            }

            string Get(string column)
            {
                var index = columns[column];
                return index < fields.Length ? fields[index] : throw new InvalidDataException(
                    $"Benchmark report row {rows.Count + 2} is missing the '{column}' value.");
            }

            var workload = Get("Workload");
            var method = Get("Method");
            var partitions = ParseInt(Get("Partitions"), $"{method}/{workload} partitions");
            ValidateCase(workload, method, partitions);
            rows.Add(new ReportRow(
                new CaseKey(workload, method, partitions),
                ParseDurationMilliseconds(Get("Mean"), "Mean"),
                ParseDurationMilliseconds(Get("Error"), "Error"),
                ParseBytes(Get(allocatedColumn.Key), "Allocated Memory")));
        }

        return rows;
    }

    private static List<DiagnosticRecord> ReadDiagnostics(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Benchmark log was not found.", path);
        }

        var diagnostics = new List<DiagnosticRecord>();
        var lineNumber = 0;
        foreach (var line in File.ReadLines(path))
        {
            lineNumber++;
            const string prefix = "PARTITION_DIAGNOSTICS_JSON ";
            if (!line.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            var json = line[prefix.Length..].Trim();
            if (json.Length == 0)
            {
                throw new InvalidDataException($"Diagnostic line {lineNumber} has no JSON payload.");
            }

            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException($"Diagnostic line {lineNumber} is not a JSON object.");
            }

            foreach (var required in DiagnosticRecord.RequiredProperties)
            {
                if (!root.TryGetProperty(required, out _))
                {
                    throw new InvalidDataException($"Diagnostic line {lineNumber} is missing '{required}'.");
                }
            }

            var record = JsonSerializer.Deserialize<DiagnosticRecord>(json, JsonOptions)
                ?? throw new InvalidDataException($"Diagnostic line {lineNumber} could not be parsed.");
            record = record with { RawJson = json };
            ValidateDiagnostic(record, lineNumber);
            diagnostics.Add(record);
        }

        if (diagnostics.Count != ExpectedDiagnosticRecordCount)
        {
            throw new InvalidDataException(
                $"Expected {ExpectedDiagnosticRecordCount} diagnostic records, found {diagnostics.Count}.");
        }

        return diagnostics;
    }

    private static List<SummaryRow> BuildSummaries(
        IReadOnlyList<ReportRow> reportRows,
        IReadOnlyList<DiagnosticRecord> diagnostics)
    {
        var reportGroups = reportRows.GroupBy(static row => row.Key).ToDictionary(static group => group.Key);
        var diagnosticGroups = diagnostics.GroupBy(static item => item.Key).ToDictionary(static group => group.Key);
        var methods = reportRows.Select(static row => row.Key.Method).Distinct(StringComparer.Ordinal).ToArray();
        if (methods.Length != 1)
        {
            throw new InvalidDataException("Each input must contain exactly one API method; do not mix SingleSubmissions and ExplicitSubmissions.");
        }

        var expectedKeys = CreateExpectedKeys(methods[0]).ToHashSet();
        if (reportRows.Count != ExpectedCaseCount || reportGroups.Count != ExpectedCaseCount)
        {
            throw new InvalidDataException(
                $"Expected one aggregate report row for each of {ExpectedCaseCount} cases; found {reportRows.Count} rows across {reportGroups.Count} cases.");
        }

        if (diagnosticGroups.Count != ExpectedCaseCount)
        {
            throw new InvalidDataException($"Expected diagnostics for {ExpectedCaseCount} cases, found {diagnosticGroups.Count}.");
        }

        if (!reportGroups.Keys.ToHashSet().SetEquals(expectedKeys) ||
            !diagnosticGroups.Keys.ToHashSet().SetEquals(expectedKeys))
        {
            throw new InvalidDataException($"Report and diagnostic case coverage does not exactly match the {ExpectedCaseCount} expected cases.");
        }

        var summaries = new List<SummaryRow>(ExpectedCaseCount);
        foreach (var key in expectedKeys.OrderBy(static item => item.Workload).ThenBy(static item => item.Method).ThenBy(static item => item.Partitions))
        {
            var reports = reportGroups[key].ToArray();
            var records = diagnosticGroups[key].ToArray();
            if (reports.Length != 1 || records.Length != ExpectedDiagnosticRecordsPerCase)
            {
                throw new InvalidDataException($"Case {key} must have exactly one report row and two diagnostic records.");
            }

            var requestCount = RequestsPerWave(key.Workload);
            var totalWaves = records.Sum(static item => item.WaveCount);
            var totalRequests = records.Sum(static item => item.ProcessedRequests);
            var totalCalls = records.Sum(static item => item.HandlerCalls);
            var fullBatches = records.Sum(static item => item.FullBatches);
            var queriedItems = records.Sum(static item => item.QueriedItems);
            var meanHandlerMilliseconds = records.Sum(static item => item.MeanHandlerMs * item.HandlerCalls) / totalCalls;
            var totalWaveMilliseconds = records.Sum(static item => item.MeanWaveMs * item.WaveCount);
            var meanWaveMilliseconds = totalWaveMilliseconds / totalWaves;
            var meanMilliseconds = reports.Average(static item => item.MeanMs);
            if (!double.IsFinite(meanMilliseconds) || meanMilliseconds <= 0)
            {
                throw new InvalidDataException($"Case {key} has a non-positive benchmark mean.");
            }
            var errorMilliseconds = reports.Average(static item => item.ErrorMs);
            var allocatedBytes = reports.Average(static item => item.AllocatedBytes);
            var peakConcurrency = records.Max(static item => item.PeakConcurrency);

            summaries.Add(new SummaryRow(
                key.Workload,
                key.Method,
                key.Partitions,
                requestCount,
                meanMilliseconds,
                errorMilliseconds,
                requestCount * 1_000.0 / meanMilliseconds,
                ExecuteCallsPerWave(key.Workload, key.Method),
                ExecuteCallsPerWave(key.Workload, key.Method) * 1_000.0 / meanMilliseconds,
                totalCalls * 1_000.0 / totalWaveMilliseconds,
                allocatedBytes,
                allocatedBytes / requestCount,
                totalCalls / (double)totalWaves,
                totalRequests / (double)totalCalls,
                fullBatches / (double)totalCalls,
                queriedItems / (double)totalWaves,
                peakConcurrency,
                meanHandlerMilliseconds,
                meanWaveMilliseconds,
                totalWaves));
        }

        return summaries;
    }

    private static void ValidateDiagnostic(DiagnosticRecord record, int lineNumber)
    {
        ValidateCase(record.Workload, record.Method, record.Partitions);
        var requestCount = RequestsPerWave(record.Workload);
        if (record.WaveCount <= 0 || record.ProcessedRequests != record.WaveCount * requestCount ||
            record.HandlerCalls <= 0 || record.PeakConcurrency is < 1 or > MaxConcurrency ||
            record.QueriedItems <= 0 || record.QueriedItems > record.ProcessedRequests ||
            !record.ExactlyOnce || !record.ResponsesValidated ||
            !double.IsFinite(record.MeanHandlerMs) || record.MeanHandlerMs < 0 ||
            !double.IsFinite(record.MeanWaveMs) || record.MeanWaveMs < 0)
        {
            throw new InvalidDataException($"Diagnostic line {lineNumber} failed its scalar validation.");
        }

        if (record.BatchSizeHistogram is not { Length: BatchSize + 1 } histogram || histogram.Any(static count => count < 0) ||
            histogram.Sum() != record.HandlerCalls ||
            histogram.Select((count, size) => (count, size)).Sum(static item => item.count * item.size) != record.ProcessedRequests ||
            histogram[BatchSize] != record.FullBatches)
        {
            throw new InvalidDataException($"Diagnostic line {lineNumber} has an invalid batch-size histogram.");
        }

        if (record.Workload != "RepeatedKeys" && record.QueriedItems != record.ProcessedRequests)
        {
            throw new InvalidDataException($"Diagnostic line {lineNumber} did not query each unique request for {record.Workload}.");
        }
    }

    private static void ValidateCase(string workload, string method, int partitions)
    {
        if (!Workloads.Contains(workload, StringComparer.Ordinal) ||
            !Methods.Contains(method, StringComparer.Ordinal) ||
            !PartitionCounts.Contains(partitions))
        {
            throw new InvalidDataException($"Unexpected benchmark case: {method}/{workload}/{partitions}.");
        }
    }

    private static IEnumerable<CaseKey> CreateExpectedKeys(string method)
    {
        foreach (var workload in Workloads)
        {
            foreach (var partitions in PartitionCounts)
            {
                yield return new CaseKey(workload, method, partitions);
            }
        }
    }

    private static int RequestsPerWave(string workload) => workload == "SmallBurst" ? 96 : 4_096;

    private static int ExecuteCallsPerWave(string workload, string method)
    {
        var requestsPerProducer = RequestsPerWave(workload) / 8;
        return method == "SingleSubmissions" ? RequestsPerWave(workload) : 8 * ((requestsPerProducer + 31) / 32);
    }

    private static int ParseInt(string value, string field)
    {
        if (!int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) || result < 0)
        {
            throw new InvalidDataException($"Invalid {field}: '{value}'.");
        }

        return result;
    }

    private static double ParseDurationMilliseconds(string value, string field)
    {
        var (number, unit) = ParseMeasurement(value, field);
        var multiplier = unit.ToLowerInvariant() switch
        {
            "ns" => 0.000001,
            "us" or "µs" or "μs" => 0.001,
            "ms" or "" => 1,
            "s" => 1_000,
            _ => throw new InvalidDataException($"Unsupported {field} unit in '{value}'."),
        };
        return number * multiplier;
    }

    private static double ParseBytes(string value, string field)
    {
        if (value.Trim() == "-")
        {
            return 0;
        }

        var (number, unit) = ParseMeasurement(value, field);
        var multiplier = unit.ToUpperInvariant() switch
        {
            "B" or "" => 1,
            "KB" => 1_024,
            "MB" => 1_048_576,
            _ => throw new InvalidDataException($"Unsupported {field} unit in '{value}'."),
        };
        return number * multiplier;
    }

    private static (double Number, string Unit) ParseMeasurement(string value, string field)
    {
        var match = MeasurementPattern.Match(value);
        if (!match.Success || !double.TryParse(match.Groups["value"].Value, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var number) ||
            !double.IsFinite(number) || number < 0)
        {
            throw new InvalidDataException($"Invalid {field} measurement: '{value}'.");
        }

        return (number, match.Groups["unit"].Value);
    }

    private static void WriteSummary(string path, IReadOnlyList<SummaryRow> rows)
    {
        using var writer = new StreamWriter(path, false, new UTF8Encoding(false));
        writer.WriteLine("Workload,WorkloadIndex,Method,Partitions,RequestsPerWave,MeanMs,ErrorMs,ThroughputRequestsPerSecond,ExecuteCallsPerWave,ExecuteCallsPerSecond,HandlerBatchesPerSecond,AllocatedBytesPerWave,BytesPerRequest,HandlerCallsPerWave,MeanBatchSize,FullBatchFraction,QueriedItemsPerWave,PeakConcurrency,MeanHandlerMs,MeanWaveMs,DiagnosticWaves");
        foreach (var row in rows)
        {
            writer.WriteLine(string.Join(",", new[]
            {
                Csv(row.Workload), (Array.IndexOf(Workloads, row.Workload) + 1).ToString(CultureInfo.InvariantCulture),
                Csv(row.Method), row.Partitions.ToString(CultureInfo.InvariantCulture),
                row.RequestsPerWave.ToString(CultureInfo.InvariantCulture), Number(row.MeanMs), Number(row.ErrorMs),
                Number(row.ThroughputRequestsPerSecond), row.ExecuteCallsPerWave.ToString(CultureInfo.InvariantCulture),
                Number(row.ExecuteCallsPerSecond), Number(row.HandlerBatchesPerSecond),
                Number(row.AllocatedBytesPerWave), Number(row.BytesPerRequest),
                Number(row.HandlerCallsPerWave), Number(row.MeanBatchSize), Number(row.FullBatchFraction),
                Number(row.QueriedItemsPerWave), row.PeakConcurrency.ToString(CultureInfo.InvariantCulture),
                Number(row.MeanHandlerMs), Number(row.MeanWaveMs), row.DiagnosticWaves.ToString(CultureInfo.InvariantCulture),
            }));
        }
    }

    private static void WriteDiagnostics(string path, IReadOnlyList<DiagnosticRecord> records)
    {
        using var writer = new StreamWriter(path, false, new UTF8Encoding(false));
        foreach (var record in records)
        {
            writer.WriteLine(record.RawJson);
        }
    }

    private static string Number(double value) => value.ToString("G17", CultureInfo.InvariantCulture);

    private static string Csv(string value) => $"\"{value.Replace("\"", "\"\"")}\"";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
    };

    private readonly record struct CaseKey(string Workload, string Method, int Partitions)
    {
        public override string ToString() => $"{Method}/{Workload}/{Partitions}";
    }

    private sealed record ReportRow(CaseKey Key, double MeanMs, double ErrorMs, double AllocatedBytes);

    private sealed record SummaryRow(
        string Workload,
        string Method,
        int Partitions,
        int RequestsPerWave,
        double MeanMs,
        double ErrorMs,
        double ThroughputRequestsPerSecond,
        int ExecuteCallsPerWave,
        double ExecuteCallsPerSecond,
        double HandlerBatchesPerSecond,
        double AllocatedBytesPerWave,
        double BytesPerRequest,
        double HandlerCallsPerWave,
        double MeanBatchSize,
        double FullBatchFraction,
        double QueriedItemsPerWave,
        int PeakConcurrency,
        double MeanHandlerMs,
        double MeanWaveMs,
        long DiagnosticWaves);

    private sealed record DiagnosticRecord
    {
        public static readonly string[] RequiredProperties = [
            "Workload", "Partitions", "Method", "WaveCount", "ProcessedRequests", "HandlerCalls",
            "QueriedItems", "FullBatches", "BatchSizeHistogram", "PeakConcurrency", "MeanHandlerMs",
            "MeanWaveMs", "ExactlyOnce", "ResponsesValidated"];

        public string Workload { get; init; } = "";
        public int Partitions { get; init; }
        public string Method { get; init; } = "";
        public long WaveCount { get; init; }
        public long ProcessedRequests { get; init; }
        public long HandlerCalls { get; init; }
        public long QueriedItems { get; init; }
        public long FullBatches { get; init; }
        public long[] BatchSizeHistogram { get; init; } = [];
        public int PeakConcurrency { get; init; }
        public double MeanHandlerMs { get; init; }
        public double MeanWaveMs { get; init; }
        public bool ExactlyOnce { get; init; }
        public bool ResponsesValidated { get; init; }

        [JsonIgnore]
        public string RawJson { get; init; } = "";

        [JsonIgnore]
        public CaseKey Key => new(Workload, Method, Partitions);
    }
}
