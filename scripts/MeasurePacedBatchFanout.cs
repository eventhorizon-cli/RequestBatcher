#:property TargetFramework=net10.0
#:property PublishAot=false
#:property LangVersion=12.0
#:project ../src/RequestBatcher/RequestBatcher.csproj
#:project Common/RequestBatcher.ScriptUtilities.csproj

using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RequestBatcher;
using RequestBatcher.ScriptUtilities;

internal static class MeasurePacedBatchFanout
{
    private static async Task<int> Main(string[] args)
    {
        if (args is ["--help"])
        {
            Console.WriteLine("""
                MeasurePacedBatchFanout --mode direct|batcher --output PATH
                  [--rate 500] [--seconds 20] [--warmup-seconds 3]
                  [--submission-size 20] [--batch-size 120] [--concurrency 2]
                  [--downstream-batch-size 30] [--delay-ms 20] [--pending-capacity 65536]
                Open-loop submissions of unique items; queries simulate I/O using Task.Delay.
                Direct mode queries each submission, splitting it into parallel calls only if it exceeds
                --downstream-batch-size. Batcher mode combines queued items before applying the same limit.
                Writes JSON and a .samples.csv sidecar. Requires .NET 10; use --configuration Release.
                Bounds: rate 1..10000, seconds 1..300, warmup 0..30, concurrency 1..256,
                sizes/capacity 1..65536, delay 0..60000 ms, at most 500000 submissions and
                10000000 items per phase. Each phase has a 120-second additional drain timeout.
                No external services. Delays are fixed per query; no random latency distribution.
                """);
            return 0;
        }

        try
        {
            var options = Options.Parse(args);
            var holder = new StateHolder();
            ServiceProvider? provider = null;
            IRequestBatcher<int, long>? batcher = null;
            if (options.Mode == "batcher")
            {
                var services = new ServiceCollection();
                services.AddSingleton(holder);
                services.AddRequestBatcher<int, long, ProbeHandler>(ServiceLifetime.Singleton, settings =>
                {
                    settings.BatchSize = options.BatchSize;
                    settings.MaxConcurrency = options.Concurrency;
                    settings.MaxPendingRequests = options.PendingCapacity;
                });
                provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
                batcher = provider.GetRequiredService<IRequestBatcher<int, long>>();
            }

            try
            {
                if (options.WarmupSeconds > 0)
                {
                    await RunAsync(options, options.WarmupSeconds, holder, batcher).ConfigureAwait(false);
                }

                var run = await RunAsync(options, options.Seconds, holder, batcher).ConfigureAwait(false);
                var report = CreateReport(options, run);
                var output = Path.GetFullPath(options.Output);
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                await File.WriteAllTextAsync(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true })).ConfigureAwait(false);
                await WriteSamplesAsync(Path.ChangeExtension(output, ".samples.csv"), run).ConfigureAwait(false);
                Console.WriteLine(JsonSerializer.Serialize(report));
            }
            finally
            {
                if (provider is not null)
                {
                    await provider.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
                }
            }
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static async Task<Run> RunAsync(Options options, int seconds, StateHolder holder, IRequestBatcher<int, long>? batcher)
    {
        var count = checked(options.Rate * seconds);
        var state = new Run(count, options);
        holder.Current = state;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(seconds + 120));
        state.CancellationToken = cancellation.Token;
        var submitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var producer = new Thread(() =>
        {
            try
            {
                for (var index = 0; index < count; index++)
                {
                    var target = state.StartTicks + (long)Math.Round((double)index * Stopwatch.Frequency / options.Rate);
                    state.Scheduled[index] = target;
                    WaitUntil(target, cancellation.Token);
                    state.Sent[index] = Stopwatch.GetTimestamp();
                    state.Tasks[index] = CompleteSubmissionAsync(state, index, batcher);
                }
                submitted.SetResult();
            }
            catch (Exception error)
            {
                submitted.SetException(error);
            }
        })
        { IsBackground = true, Name = "Paced batch producer" };

        // Setup and request/sample arrays are excluded; producer and observation overhead remain included.
        state.AllocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        using var process = Process.GetCurrentProcess();
        state.CpuBefore = process.TotalProcessorTime;
        state.StartTicks = Stopwatch.GetTimestamp();
        producer.Start();
        try
        {
            await submitted.Task.WaitAsync(cancellation.Token).ConfigureAwait(false);
            await Task.WhenAll(state.Tasks).WaitAsync(cancellation.Token).ConfigureAwait(false);
        }
        finally
        {
            cancellation.Cancel();
        }
        state.FinishTicks = state.Completed.Max();
        state.AllocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - state.AllocatedBefore;
        state.CpuMilliseconds = (process.TotalProcessorTime - state.CpuBefore).TotalMilliseconds;
        if (state.QueryCounts.Any(value => value != 1) || state.Completed.Any(value => value == 0) ||
            state.Downstream.Active != 0 || state.Handler.Active != 0 ||
            state.Downstream.Items != count * options.SubmissionSize ||
            state.Downstream.MaximumSize > options.DownstreamBatchSize ||
            (batcher is not null && (state.Handler.Items != count * options.SubmissionSize || state.Handler.Peak > options.Concurrency ||
                state.HandlerCounts.Any(value => value != 1) ||
                state.Handler.MaximumSize > options.BatchSize ||
                state.Downstream.Peak > options.Concurrency * (int)Math.Ceiling(options.BatchSize / (double)options.DownstreamBatchSize))))
        {
            throw new InvalidOperationException("Response, exactly-once, item-count, drain, or concurrency validation failed.");
        }
        return state;
    }

    private static async Task CompleteSubmissionAsync(Run state, int index, IRequestBatcher<int, long>? batcher)
    {
        var requests = state.Requests[index];
        IReadOnlyList<long> responses;
        if (batcher is null)
        {
            if (requests.Length <= state.Options.DownstreamBatchSize)
            {
                var values = await QueryAsync(state, requests).ConfigureAwait(false);
                responses = values.Select(value => value.Value).ToArray();
            }
            else
            {
                var calls = requests.Chunk(state.Options.DownstreamBatchSize).Select(chunk => QueryAsync(state, chunk)).ToArray();
                var values = await Task.WhenAll(calls).ConfigureAwait(false);
                responses = values.SelectMany(chunk => chunk).Select(value => value.Value).ToArray();
            }
        }
        else
        {
            responses = await batcher.ExecuteAsync(requests, state.CancellationToken).ConfigureAwait(false);
        }
        state.Completed[index] = Stopwatch.GetTimestamp();
        if (responses.Count != requests.Length)
        {
            throw new InvalidOperationException("Response count mismatch.");
        }
        for (var item = 0; item < requests.Length; item++)
        {
            if (responses[item] != Transform(requests[item]))
            {
                throw new InvalidOperationException($"Response value/order mismatch for submission {index}, item {item}.");
            }
        }
    }

    private static async Task<KeyValuePair<int, long>[]> QueryAsync(Run state, int[] requests)
    {
        if (requests.Length < 1 || requests.Length > state.Options.DownstreamBatchSize)
        {
            throw new InvalidOperationException("Invalid downstream query size.");
        }
        var start = state.Downstream.Start(requests.Length);
        try
        {
            foreach (var request in requests)
            {
                if (Interlocked.Increment(ref state.QueryCounts[request]) != 1)
                {
                    throw new InvalidOperationException($"Duplicate handling for item {request}.");
                }
            }
            await Task.Delay(state.Options.DelayMilliseconds, state.CancellationToken).ConfigureAwait(false);
            return requests.Select(request => new KeyValuePair<int, long>(request, Transform(request))).ToArray();
        }
        finally
        {
            state.Downstream.End(start);
        }
    }

    private static long Transform(int id) => (long)id * 17 + 3;

    private static void WaitUntil(long target, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remainingMilliseconds = (target - Stopwatch.GetTimestamp()) * 1000.0 / Stopwatch.Frequency;
            if (remainingMilliseconds <= 0)
            {
                return;
            }
            if (remainingMilliseconds > 1.5)
            {
                Thread.Sleep(Math.Max(1, (int)remainingMilliseconds - 1));
            }
            else
            {
                Thread.SpinWait(64);
            }
        }
    }

    private static object CreateReport(Options options, Run run)
    {
        var wallSeconds = (run.FinishTicks - run.StartTicks) / (double)Stopwatch.Frequency;
        var issueSpanSeconds = (run.Sent[^1] - run.Sent[0]) / (double)Stopwatch.Frequency;
        var actualLatency = run.Completed.Zip(run.Sent, Milliseconds).ToArray();
        var scheduledLatency = run.Completed.Zip(run.Scheduled, Milliseconds).ToArray();
        var lateness = run.Sent.Zip(run.Scheduled, Milliseconds).ToArray();
        var intervals = run.Sent.Skip(1).Zip(run.Sent, Milliseconds).ToArray();
        var quarter = Math.Max(1, actualLatency.Length / 4);
        return new
        {
            Runtime = RuntimeInformation.FrameworkDescription,
            OperatingSystem = RuntimeInformation.OSDescription,
            Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            ProcessorCount = Environment.ProcessorCount,
#if DEBUG
            Configuration = "Debug",
#else
            Configuration = "Release",
#endif
            Options = options,
            options.SubmissionSize,
            options.DownstreamBatchSize,
            SimulatedDownstreamMilliseconds = options.DelayMilliseconds,
            PartitionCount = 1,
            options.PendingCapacity,
            Observability = "No ActivityListener, MeterListener, or OpenTelemetry provider",
            Validation = "Every response value and explicit response order checked; every item queried exactly once; all calls drained",
            Submissions = run.Requests.Length,
            CompletedSubmissions = run.Completed.Count(value => value != 0),
            Items = run.QueryCounts.Length,
            IssueSpanSeconds = issueSpanSeconds,
            ActualSubmissionRate = run.Requests.Length > 1 ? (run.Requests.Length - 1) / issueSpanSeconds : (double?)null,
            CompletionSecondsIncludingDrain = wallSeconds,
            CompletedSubmissionsPerSecond = run.Requests.Length / wallSeconds,
            CompletedItemsPerSecond = run.QueryCounts.Length / wallSeconds,
            DrainMillisecondsAfterLastSubmission = Milliseconds(run.FinishTicks, run.Sent[^1]),
            LatencyMilliseconds = Describe(actualLatency),
            ScheduledLatencyMilliseconds = Describe(scheduledLatency),
            FirstQuarterLatencyMilliseconds = Describe(actualLatency.Take(quarter).ToArray()),
            LastQuarterLatencyMilliseconds = Describe(actualLatency.TakeLast(quarter).ToArray()),
            ProducerLatenessMilliseconds = Describe(lateness),
            InterarrivalMilliseconds = Describe(intervals),
            Handler = run.Handler.Snapshot(wallSeconds),
            Downstream = run.Downstream.Snapshot(wallSeconds),
            ProcessAllocatedBytes = run.AllocatedBytes,
            AllocatedBytesPerItem = run.AllocatedBytes / (double)run.QueryCounts.Length,
            ProcessCpuMilliseconds = run.CpuMilliseconds,
            MeasurementNotes = "In-process Task.Delay models I/O, not a real database. Allocation/CPU include the generator, timers, instrumentation and validation. Actual latency starts immediately before ExecuteAsync or direct query; scheduled latency also includes generator lateness. Nearest-rank percentiles. No deduplication savings: all item IDs are unique."
        };
    }

    private static double Milliseconds(long end, long start) => (end - start) * 1000.0 / Stopwatch.Frequency;

    private static SampleSummary Describe(double[] values) => SampleSummary.Calculate(values);

    private static async Task WriteSamplesAsync(string path, Run run)
    {
        var builder = new StringBuilder("submission,scheduled_ms,sent_ms,completed_ms,latency_ms,scheduled_latency_ms,lateness_ms\n");
        for (var index = 0; index < run.Requests.Length; index++)
        {
            builder.AppendLine(FormattableString.Invariant($"{index},{Milliseconds(run.Scheduled[index], run.StartTicks):F6},{Milliseconds(run.Sent[index], run.StartTicks):F6},{Milliseconds(run.Completed[index], run.StartTicks):F6},{Milliseconds(run.Completed[index], run.Sent[index]):F6},{Milliseconds(run.Completed[index], run.Scheduled[index]):F6},{Milliseconds(run.Sent[index], run.Scheduled[index]):F6}"));
        }
        await File.WriteAllTextAsync(path, builder.ToString()).ConfigureAwait(false);
    }

    private sealed record Options(
        string Mode, string Output, int Rate = 500, int Seconds = 20, int WarmupSeconds = 3,
        int BatchSize = 120, int Concurrency = 2, int SubmissionSize = 20, int DownstreamBatchSize = 30,
        int DelayMilliseconds = 20, int PendingCapacity = 65_536)
    {
        public static Options Parse(string[] args)
        {
            if (args.Length % 2 != 0)
            {
                throw new ArgumentException("Arguments must be --name value pairs. Use --help.");
            }
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var index = 0; index < args.Length; index += 2)
            {
                if (args[index] is not ("--mode" or "--output" or "--rate" or "--seconds" or "--warmup-seconds" or
                    "--batch-size" or "--concurrency" or "--submission-size" or "--downstream-batch-size" or
                    "--delay-ms" or "--pending-capacity") || !values.TryAdd(args[index], args[index + 1]))
                {
                    throw new ArgumentException($"Unknown or duplicate argument: {args[index]}");
                }
            }
            int Number(string name, int fallback) => values.TryGetValue(name, out var value) ? int.Parse(value, CultureInfo.InvariantCulture) : fallback;
            var result = new Options(
                values.GetValueOrDefault("--mode", "batcher"), values.GetValueOrDefault("--output", ""),
                Number("--rate", 500), Number("--seconds", 20), Number("--warmup-seconds", 3),
                Number("--batch-size", 120), Number("--concurrency", 2), Number("--submission-size", 20),
                Number("--downstream-batch-size", 30), Number("--delay-ms", 20), Number("--pending-capacity", 65_536));
            if (result.Mode is not ("direct" or "batcher") || string.IsNullOrWhiteSpace(result.Output) ||
                result.Rate is < 1 or > 10_000 || result.Seconds is < 1 or > 300 || result.WarmupSeconds is < 0 or > 30 ||
                result.BatchSize is < 1 or > 65_536 || result.Concurrency is < 1 or > 256 ||
                result.SubmissionSize is < 1 or > 65_536 || result.DownstreamBatchSize is < 1 or > 65_536 ||
                result.DelayMilliseconds is < 0 or > 60_000 || result.PendingCapacity is < 1 or > 65_536 ||
                (long)result.Rate * Math.Max(result.Seconds, result.WarmupSeconds) > 500_000 ||
                (long)result.Rate * Math.Max(result.Seconds, result.WarmupSeconds) * result.SubmissionSize > 10_000_000)
            {
                throw new ArgumentException("Invalid mode, output or numeric range. Use --help for limits.");
            }
            return result;
        }
    }

    private sealed class Run
    {
        public Run(int submissions, Options options)
        {
            Options = options;
            Requests = Enumerable.Range(0, submissions).Select(index => Enumerable.Range(index * options.SubmissionSize, options.SubmissionSize).ToArray()).ToArray();
            Tasks = new Task[submissions];
            Scheduled = new long[submissions];
            Sent = new long[submissions];
            Completed = new long[submissions];
            QueryCounts = new int[submissions * options.SubmissionSize];
            HandlerCounts = new int[submissions * options.SubmissionSize];
        }

        public Options Options { get; }
        public int[][] Requests { get; }
        public Task[] Tasks { get; }
        public long[] Scheduled { get; }
        public long[] Sent { get; }
        public long[] Completed { get; }
        public int[] QueryCounts { get; }
        public int[] HandlerCounts { get; }
        public CallMetrics Handler { get; } = new();
        public CallMetrics Downstream { get; } = new();
        public CancellationToken CancellationToken { get; set; }
        public long StartTicks { get; set; }
        public long FinishTicks { get; set; }
        public long AllocatedBefore { get; set; }
        public long AllocatedBytes { get; set; }
        public TimeSpan CpuBefore { get; set; }
        public double CpuMilliseconds { get; set; }
    }

    private sealed class StateHolder
    {
        public Run Current { get; set; } = null!;
    }

    private sealed class ProbeHandler(StateHolder holder) : IRequestBatchHandler<int, long>
    {
        public async ValueTask HandleAsync(IReadOnlyList<RequestBatchEntry<int, long>> entries, CancellationToken cancellationToken = default)
        {
            var state = holder.Current;
            var start = state.Handler.Start(entries.Count);
            try
            {
                foreach (var entry in entries)
                {
                    if (Interlocked.Increment(ref state.HandlerCounts[entry.Request]) != 1)
                    {
                        throw new InvalidOperationException($"Duplicate Handler entry for item {entry.Request}.");
                    }
                }
                var ids = entries.Select(entry => entry.Request).Distinct().ToArray();
                IEnumerable<KeyValuePair<int, long>> responses;
                if (ids.Length <= state.Options.DownstreamBatchSize)
                {
                    responses = await QueryAsync(state, ids).ConfigureAwait(false);
                }
                else
                {
                    var tasks = ids.Chunk(state.Options.DownstreamBatchSize).Select(chunk => QueryAsync(state, chunk)).ToArray();
                    var results = await Task.WhenAll(tasks).ConfigureAwait(false);
                    responses = results.SelectMany(result => result);
                }
                var byId = responses.ToDictionary(result => result.Key, result => result.Value);
                entries.SetResponses(request => byId[request]);
            }
            finally
            {
                state.Handler.End(start);
            }
        }
    }

    private sealed class CallMetrics
    {
        private readonly object _gate = new();
        private readonly List<double> _durations = [];
        private readonly SortedDictionary<int, int> _sizes = new();
        private long _ticks;
        public int Active { get; private set; }
        public int Peak { get; private set; }
        public long Items { get; private set; }
        public int MaximumSize => _sizes.Count == 0 ? 0 : _sizes.Keys.Last();

        public long Start(int size)
        {
            lock (_gate)
            {
                Active++;
                Peak = Math.Max(Peak, Active);
                Items += size;
                _sizes[size] = _sizes.GetValueOrDefault(size) + 1;
                return Stopwatch.GetTimestamp();
            }
        }

        public void End(long start)
        {
            var elapsed = Stopwatch.GetTimestamp() - start;
            lock (_gate)
            {
                Active--;
                _ticks += elapsed;
                _durations.Add(elapsed * 1000.0 / Stopwatch.Frequency);
            }
        }

        public object Snapshot(double seconds) => new
        {
            Calls = _durations.Count,
            Items,
            MeanSize = _durations.Count == 0 ? 0 : Items / (double)_durations.Count,
            SizeHistogram = _sizes,
            PeakConcurrency = Peak,
            MeanConcurrency = _ticks / (double)Stopwatch.Frequency / seconds,
            CallsPerSecond = _durations.Count / seconds,
            DurationMilliseconds = Describe(_durations.ToArray())
        };
    }
}
