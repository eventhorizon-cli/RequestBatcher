using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RequestBatcher.Scheduling;

namespace RequestBatcher.Evaluation;

internal static class Program
{
    private static async Task Main(string[] args)
    {
        var settings = new Settings(args[0], int.Parse(args[1]), int.Parse(args[2]), int.Parse(args[3]),
            bool.Parse(args[4]), int.Parse(args[5]), args[6], int.Parse(args[7]));
        RequestBatcherServiceCollectionExtensions.EvaluationPartitions = settings.Partitions;
        BatchDispatchLoop<Work>.EvaluationOffload = settings.Offload;
        if (settings.Kind == "burst")
        {
            Console.WriteLine(JsonSerializer.Serialize(await BurstAsync(settings)));
            return;
        }

        await MeasureAsync(settings);
        var measurements = new List<object>();
        for (var iteration = 0; iteration < 3; iteration++)
        {
            measurements.Add(await MeasureAsync(settings));
        }

        Console.WriteLine(JsonSerializer.Serialize(new
        {
            Settings = settings,
            Runtime = RuntimeInformation.FrameworkDescription,
            Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            CpuCount = Environment.ProcessorCount,
            Measurements = measurements,
        }));
    }

    private static ServiceProvider CreateProvider(Metrics metrics)
    {
        var services = new ServiceCollection();
        services.AddSingleton(metrics);
        services.AddRequestBatcher<Work, Handler>(ServiceLifetime.Singleton, options =>
        {
            options.BatchSize = metrics.Settings.BatchSize;
            options.MaxConcurrency = metrics.Settings.Concurrency;
            options.MaxPendingRequests = 8192;
            if (metrics.Settings.Keys != "none")
            {
                options.UsePartitionKey(work => work.Key);
            }
        });
        return services.BuildServiceProvider();
    }

    private static Work[] CreateWork(Settings settings)
    {
        var random = new Random(7824);
        var requests = Enumerable.Range(0, settings.Count)
            .Select(i => new Work($"user:{(settings.Keys == "skew" && i % 10 != 0 ? 0 : i % 256):D4}"))
            .ToArray();
        random.Shuffle(requests);
        return requests;
    }

    private static async Task<object> MeasureAsync(Settings settings)
    {
        var metrics = new Metrics(settings);
        var requests = CreateWork(settings);
        await using var provider = CreateProvider(metrics);
        var batcher = provider.GetRequiredService<IRequestBatcher<Work>>();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        var allocated = GC.GetTotalAllocatedBytes(true);
        var collections = Enumerable.Range(0, 3).Select(GC.CollectionCount).ToArray();
        var stopwatch = Stopwatch.StartNew();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var producers = Enumerable.Range(0, settings.Producers).Select(producer => Task.Run(async () =>
        {
            await start.Task.ConfigureAwait(false);
            var pending = new List<Task>(1024);
            for (var index = producer; index < requests.Length; index += settings.Producers)
            {
                requests[index].Submitted = Stopwatch.GetTimestamp();
                pending.Add(batcher.ProcessAsync(requests[index]));
                if (pending.Count == 1024)
                {
                    await Task.WhenAll(pending).ConfigureAwait(false);
                    pending.Clear();
                }
            }
            await Task.WhenAll(pending).ConfigureAwait(false);
        })).ToArray();
        start.SetResult();
        await Task.WhenAll(producers).WaitAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(false);
        stopwatch.Stop();
        var allocatedDelta = GC.GetTotalAllocatedBytes(true) - allocated;
        var gc = Enumerable.Range(0, 3).Select(i => GC.CollectionCount(i) - collections[i]).ToArray();
        await provider.GetRequiredService<RequestBatchCoordinator<Work>>().StopAsync().ConfigureAwait(false);
        if (metrics.Handled != settings.Count || requests.Any(work => work.Visits != 1))
        {
            throw new InvalidOperationException("Missing or duplicate request processing.");
        }
        var waits = requests.Select(work => (work.Started - work.Submitted) * 1000.0 / Stopwatch.Frequency)
            .Order().ToArray();
        return new
        {
            Milliseconds = stopwatch.Elapsed.TotalMilliseconds,
            RequestsPerSecond = settings.Count / stopwatch.Elapsed.TotalSeconds,
            metrics.Peak,
            AverageBatchSize = settings.Count / (double)metrics.Batches,
            Occupancy = metrics.BusyTicks / (double)Stopwatch.Frequency / stopwatch.Elapsed.TotalSeconds / settings.Concurrency,
            PreHandlerP99Ms = waits[Math.Min(waits.Length - 1, (int)(waits.Length * 0.99))],
            BytesPerRequest = allocatedDelta / (double)settings.Count,
            Gc = gc,
            ThreadPoolThreads = ThreadPool.ThreadCount,
            BatchSizes = metrics.Sizes.Select((count, size) => new { Size = size, Count = count }).Where(x => x.Count != 0),
        };
    }

    private static async Task<object> BurstAsync(Settings settings)
    {
        var metrics = new Metrics(settings);
        var requests = CreateWork(settings);
        await using var provider = CreateProvider(metrics);
        var batcher = provider.GetRequiredService<IRequestBatcher<Work>>();
        var blockers = new List<Task>();
        for (var i = 0; i < settings.Concurrency; i++)
        {
            var work = new Work($"block:{i}") { Gate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
            blockers.Add(batcher.ProcessAsync(work));
            await work.Gate.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        var submitted = batcher.ProcessAsync(requests);
        metrics.Release.SetResult();
        await Task.WhenAll(blockers.Append(submitted)).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        await provider.GetRequiredService<RequestBatchCoordinator<Work>>().StopAsync().ConfigureAwait(false);
        if (metrics.Handled != settings.Count || requests.Any(work => work.Visits != 1))
        {
            throw new InvalidOperationException("Missing or duplicate burst work.");
        }
        return new
        {
            Settings = settings,
            metrics.Batches,
            metrics.DistinctKeyWork,
            BatchSizes = metrics.Sizes.Select((count, size) => new { Size = size, Count = count }).Where(x => x.Count != 0),
        };
    }

    private sealed record Settings(string Kind, int BatchSize, int Concurrency, int Partitions,
        bool Offload, int Producers, string Keys, int Count);

    private sealed class Work(string key)
    {
        public string Key { get; } = key;
        public long Submitted;
        public long Started;
        public int Visits;
        public TaskCompletionSource? Gate;
    }

    private sealed class Metrics(Settings settings)
    {
        public Settings Settings { get; } = settings;
        public int Active;
        public int Peak;
        public int Batches;
        public int Handled;
        public int DistinctKeyWork;
        public int Sink;
        public long BusyTicks;
        public int[] Sizes { get; } = new int[settings.BatchSize + 1];
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class Handler(Metrics metrics) : IRequestBatchHandler<Work>
    {
        public async ValueTask HandleAsync(IReadOnlyList<Work> requests, CancellationToken cancellationToken = default)
        {
            if (requests[0].Gate is { } gate)
            {
                gate.SetResult();
                await metrics.Release.Task.ConfigureAwait(false);
                return;
            }
            var begin = Stopwatch.GetTimestamp();
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
            Interlocked.Increment(ref metrics.Sizes[requests.Count]);
            foreach (var work in requests)
            {
                work.Started = begin;
                Interlocked.Increment(ref work.Visits);
            }
            switch (metrics.Settings.Kind)
            {
                case "io":
                    await Task.Delay(1, cancellationToken).ConfigureAwait(false);
                    break;
                case "blocking":
                    Thread.Sleep(1);
                    break;
                case "cpu":
                    var value = 17;
                    for (var i = 0; i < 100_000; i++)
                    {
                        value = unchecked(value * 1_664_525 + 1_013_904_223);
                    }
                    Volatile.Write(ref metrics.Sink, value);
                    break;
                case "burst":
                    Interlocked.Add(ref metrics.DistinctKeyWork, requests.Select(work => work.Key).Distinct().Count());
                    break;
            }
            Interlocked.Add(ref metrics.BusyTicks, Stopwatch.GetTimestamp() - begin);
            Interlocked.Add(ref metrics.Handled, requests.Count);
            Interlocked.Decrement(ref metrics.Active);
        }
    }
}
