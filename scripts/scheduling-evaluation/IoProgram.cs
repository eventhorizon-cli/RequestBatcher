using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RequestBatcher.Scheduling;

namespace RequestBatcher.Evaluation;

internal static class Program
{
    private static async Task Main(string[] args)
    {
        var settings = JsonSerializer.Deserialize<Settings>(args[0])!;
        RequestBatcherServiceCollectionExtensions.EvaluationPartitions = 1;
        BatchDispatchLoop<Work>.EvaluationOffload = settings.Offload;
        using var sockets = new SocketsHttpHandler { MaxConnectionsPerServer = settings.Connections, UseProxy = false };
        using var client = new HttpClient(sockets) { BaseAddress = new Uri(args[1]), Timeout = TimeSpan.FromSeconds(30) };
        // Establish and warm the connection pool outside measurement.
        await Task.WhenAll(Enumerable.Range(0, settings.Connections).Select(async _ =>
        {
            using var response = await client.GetAsync("work?delay=10&count=0");
            response.EnsureSuccessStatusCode();
        }));
        await MeasureAsync(settings, client, settings.WarmupRequests);
        var measurements = new List<object>();
        for (var iteration = 0; iteration < settings.Iterations; iteration++)
        {
            measurements.Add(await MeasureAsync(settings, client, settings.MeasuredRequests));
        }
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            Settings = settings,
            Runtime = RuntimeInformation.FrameworkDescription,
            CpuCount = Environment.ProcessorCount,
            Measurements = measurements,
        }));
    }

    private static async Task<object> MeasureAsync(Settings settings, HttpClient client, int count)
    {
        var metrics = new Metrics(settings, client);
        var services = new ServiceCollection();
        services.AddSingleton(metrics);
        services.AddRequestBatcher<Work, Handler>(ServiceLifetime.Singleton, options =>
        {
            options.BatchSize = 32;
            options.MaxConcurrency = settings.Concurrency;
            options.MaxPendingRequests = 8192;
        });
        await using var provider = services.BuildServiceProvider();
        var batcher = provider.GetRequiredService<IRequestBatcher<Work>>();
        var work = Enumerable.Range(0, count).Select(_ => new Work()).ToArray();
        using var reset = await client.PostAsync("reset", null);
        reset.EnsureSuccessStatusCode();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        using var process = Process.GetCurrentProcess();
        var initialCpu = process.TotalProcessorTime;
        var initialAllocated = GC.GetTotalAllocatedBytes(true);
        var samples = new List<PoolSample>();
        using var sampling = new CancellationTokenSource();
        var sampler = SamplePoolAsync(samples, sampling.Token);
        var clock = Stopwatch.StartNew();
        // Fixed, bounded outstanding window. Per-item timestamps include submission through completion.
        for (var offset = 0; offset < count; offset += 1024)
        {
            if (settings.BatchSubmission)
            {
                var window = work.Skip(offset).Take(1024).ToArray();
                var submitted = Stopwatch.GetTimestamp();
                foreach (var item in window)
                {
                    item.Submitted = submitted;
                }
                await batcher.ProcessAsync(window).WaitAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(false);
                var completed = Stopwatch.GetTimestamp();
                foreach (var item in window)
                {
                    item.Completed = completed;
                }
                continue;
            }
            var tasks = new Task[Math.Min(1024, count - offset)];
            for (var i = 0; i < tasks.Length; i++)
            {
                var item = work[offset + i];
                item.Submitted = Stopwatch.GetTimestamp();
                tasks[i] = SubmitAsync(batcher, item);
            }
            await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(false);
        }
        clock.Stop();
        var allocated = GC.GetTotalAllocatedBytes(true) - initialAllocated;
        var cpu = process.TotalProcessorTime - initialCpu;
        sampling.Cancel();
        await sampler.ConfigureAwait(false);
        await provider.GetRequiredService<RequestBatchCoordinator<Work>>().StopAsync();
        var downstream = await client.GetFromJsonAsync<ServerStats>("stats");
        if (work.Any(item => item.Visits != 1) || downstream!.Items != count || downstream.Calls != metrics.Batches)
        {
            throw new InvalidOperationException("Missing or duplicate work at client or downstream.");
        }
        var total = work.Select(item => Milliseconds(item.Completed - item.Submitted)).Order().ToArray();
        var waits = work.Select(item => Milliseconds(item.Started - item.Submitted)).Order().ToArray();
        var calls = metrics.Calls.ToArray();
        if (calls.Length != metrics.Batches || calls.Sum(call => call.Items) != count)
        {
            throw new InvalidOperationException("Missing per-call timing measurements.");
        }
        return new
        {
            RequestsPerSecond = count / clock.Elapsed.TotalSeconds,
            CallsPerSecond = metrics.Batches / clock.Elapsed.TotalSeconds,
            Milliseconds = clock.Elapsed.TotalMilliseconds,
            EndToEndP50Ms = Percentile(total, 0.5),
            EndToEndP99Ms = Percentile(total, 0.99),
            PreHandlerP99Ms = Percentile(waits, 0.99),
            metrics.Peak,
            metrics.Batches,
            CpuWorkMsPerBatch = Milliseconds(metrics.CpuTicks) / metrics.Batches,
            AverageBatchSize = count / (double)metrics.Batches,
            ClientCpuCores = cpu.TotalSeconds / clock.Elapsed.TotalSeconds,
            BytesPerRequest = allocated / (double)count,
            ThreadPoolPeakThreads = samples.Max(sample => sample.Threads),
            ThreadPoolPeakPending = samples.Max(sample => sample.Pending),
            HttpCall = Summarize(calls.Select(call => call.HttpMs)),
            ServerResidence = Summarize(calls.Select(call => call.ServerMs)),
            HandlerDuration = Summarize(calls.Select(call => call.HandlerMs)),
            MeanActiveHandlers = calls.Sum(call => call.HandlerMs) / clock.Elapsed.TotalMilliseconds,
            Calls = calls,
            Downstream = downstream,
        };
    }

    private static async Task SubmitAsync(IRequestBatcher<Work> batcher, Work work)
    {
        await batcher.ProcessAsync(work).ConfigureAwait(false);
        work.Completed = Stopwatch.GetTimestamp();
    }

    private static async Task SamplePoolAsync(List<PoolSample> samples, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                samples.Add(new PoolSample(ThreadPool.ThreadCount, ThreadPool.PendingWorkItemCount));
                await Task.Delay(10, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static double Milliseconds(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
    private static double Percentile(double[] sorted, double fraction) => sorted[(int)Math.Ceiling(sorted.Length * fraction) - 1];

    private static object Summarize(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        return new
        {
            Count = sorted.Length,
            MeanMs = sorted.Average(),
            P50Ms = Percentile(sorted, 0.50),
            P95Ms = Percentile(sorted, 0.95),
            P99Ms = Percentile(sorted, 0.99),
        };
    }

    private sealed record Settings(string Kind, int DelayMs, int Concurrency, int Connections, int CpuIterations,
        bool Offload, bool BatchSubmission = false, int MeasuredRequests = 8192, int WarmupRequests = 4096,
        int Iterations = 3);
    private sealed record PoolSample(int Threads, long Pending);
    private sealed record ServerStats(int Calls, int Items, int Peak);
    private readonly record struct CallTiming(double HttpMs, double ServerMs, double HandlerMs, int Items);
    private sealed class Work
    {
        public long Submitted;
        public long Started;
        public long Completed;
        public int Visits;
    }

    private sealed class Metrics(Settings settings, HttpClient client)
    {
        public Settings Settings { get; } = settings;
        public HttpClient Client { get; } = client;
        public int Active;
        public int Peak;
        public int Batches;
        public int Sink;
        public long CpuTicks;
        public ConcurrentQueue<CallTiming> Calls { get; } = new();
    }

    private sealed class Handler(Metrics metrics) : IRequestBatchHandler<Work>
    {
        public async ValueTask HandleAsync(IReadOnlyList<Work> requests, CancellationToken cancellationToken = default)
        {
            var handlerStart = Stopwatch.GetTimestamp();
            var httpMs = 0.0;
            var serverMs = 0.0;
            var active = Interlocked.Increment(ref metrics.Active);
            var peak = Volatile.Read(ref metrics.Peak);
            while (active > peak)
            {
                var observed = Interlocked.CompareExchange(ref metrics.Peak, active, peak);
                if (observed == peak)
                {
                    break;
                }
                peak = observed;
            }
            Interlocked.Increment(ref metrics.Batches);
            foreach (var request in requests)
            {
                request.Started = Stopwatch.GetTimestamp();
                Interlocked.Increment(ref request.Visits);
            }
            try
            {
                if (metrics.Settings.Kind == "prefix")
                {
                    BurnCpu(metrics.Settings.CpuIterations);
                }
                using var message = new HttpRequestMessage(HttpMethod.Get,
                    $"work?delay={metrics.Settings.DelayMs}&count={requests.Count}")
                {
                    Version = HttpVersion.Version11,
                    VersionPolicy = HttpVersionPolicy.RequestVersionExact,
                };
                var httpStart = Stopwatch.GetTimestamp();
                using var response = metrics.Settings.Kind == "blocking"
                    ? metrics.Client.Send(message, cancellationToken)
                    : await metrics.Client.SendAsync(message, cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                if (await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false) != requests.Count.ToString())
                {
                    throw new InvalidOperationException("Downstream acknowledged an incorrect count.");
                }
                httpMs = Milliseconds(Stopwatch.GetTimestamp() - httpStart);
                serverMs = double.Parse(response.Headers.GetValues("X-Service-Duration-Ms").Single(),
                    CultureInfo.InvariantCulture);
                if (metrics.Settings.Kind == "suffix")
                {
                    BurnCpu(metrics.Settings.CpuIterations);
                }
            }
            finally
            {
                metrics.Calls.Enqueue(new CallTiming(httpMs, serverMs,
                    Milliseconds(Stopwatch.GetTimestamp() - handlerStart), requests.Count));
                Interlocked.Decrement(ref metrics.Active);
            }
        }

        private void BurnCpu(int iterations)
        {
            var begin = Stopwatch.GetTimestamp();
            var value = 17;
            for (var index = 0; index < iterations; index++)
            {
                value = unchecked(value * 1_664_525 + 1_013_904_223);
            }
            Volatile.Write(ref metrics.Sink, value);
            Interlocked.Add(ref metrics.CpuTicks, Stopwatch.GetTimestamp() - begin);
        }
    }
}
