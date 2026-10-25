using System.Diagnostics;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using Microsoft.Extensions.DependencyInjection;

namespace RequestBatcher.Benchmarks.InMemory;

/// <summary>
/// Measures one submission API at a time while varying queue partitions.
/// </summary>
/// <remarks>
/// Each benchmark method is an independent experiment with the same requests, batch size, handler concurrency and
/// simulated downstream work. The benchmark validates every response and records handler batch formation as
/// diagnostics; those diagnostic aggregates include warm-up and measurement invocations and are separate from
/// BenchmarkDotNet's reported means. The two submission methods are not a single baseline comparison.
/// </remarks>
[MemoryDiagnoser]
[SimpleJob(
    RunStrategy.Throughput,
    launchCount: 2,
    warmupCount: 3,
    iterationCount: 8,
    id: "PartitionSubmission")]
[IterationTime(300)]
public class PartitionSubmissionBenchmarks
{
    private const int BatchSize = 64;
    private const int MaxConcurrency = 4;
    private const int MaxPendingRequests = 8_192;
    private const int ProducerCount = 8;
    private const int SmallBurstCount = 96;
    private const int BacklogCount = 4_096;
    private const int RepeatedKeyCount = 128;

    private ServiceProvider _provider = null!;
    private IRequestBatcher<ProductRequest, int> _batcher = null!;
    private ProbeHandler _handler = null!;
    private ProductRequest[] _requests = null!;
    private ProductRequest[][] _producerRequests = null!;
    private ProductRequest[][][] _producerGroups = null!;
    private string _diagnosticMethod = "unknown";

    /// <summary>Gets or sets the number of BufferQueue partitions.</summary>
    [Params(1, 4)]
    public int Partitions { get; set; }

    /// <summary>Gets or sets the request distribution used by the wave.</summary>
    [Params("SmallBurst", "Backlog", "RepeatedKeys")]
    public string Workload { get; set; } = "SmallBurst";

    /// <summary>Builds and warms the provider and single-submission path outside measurement.</summary>
    [GlobalSetup(Target = nameof(SingleSubmissions))]
    public void SetupSingles() => Setup(explicitSubmission: false);

    /// <summary>Builds and warms the provider and explicit-submission path outside measurement.</summary>
    [GlobalSetup(Target = nameof(ExplicitSubmissions))]
    public void SetupExplicit() => Setup(explicitSubmission: true);

    private void Setup(bool explicitSubmission)
    {
        _requests = CreateRequests(Workload);
        _producerRequests = SplitAcrossProducers(_requests);
        _producerGroups = _producerRequests.Select(static requests => SplitIntoGroups(requests)).ToArray();
        var services = new ServiceCollection();
        services.AddRequestBatcher<ProductRequest, int, ProbeHandler>(
            ServiceLifetime.Singleton,
            options =>
            {
                options.BatchSize = BatchSize;
                options.MaxConcurrency = MaxConcurrency;
                options.MaxPendingRequests = MaxPendingRequests;
                options.FullMode = RequestBatchFullMode.Wait;
                if (Partitions > 1)
                {
                    options.UsePartitionKey(static request => request.ProductId, Partitions);
                }
            });

        _provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
        _batcher = _provider.GetRequiredService<IRequestBatcher<ProductRequest, int>>();
        _handler = (ProbeHandler)_provider.GetRequiredService<IRequestBatchHandler<ProductRequest, int>>();
        _handler.Configure(Workload);

        // Warm the selected submission API and response path before timing. Diagnostics include this warm-up.
        RunWaveAsync("Warmup", explicitSubmission).GetAwaiter().GetResult();
    }

    /// <summary>Measures eight producers submitting every request independently before awaiting their results.</summary>
    [Benchmark]
    public Task SingleSubmissions() => RunWaveAsync(nameof(SingleSubmissions), explicitSubmission: false);

    /// <summary>Measures eight producers submitting groups of at most 32 requests before awaiting their results.</summary>
    [Benchmark]
    public Task ExplicitSubmissions() => RunWaveAsync(nameof(ExplicitSubmissions), explicitSubmission: true);

    /// <summary>Drains and disposes the provider, then emits aggregate batch diagnostics.</summary>
    [GlobalCleanup]
    public async Task CleanupAsync()
    {
        await _provider.DisposeAsync().ConfigureAwait(false);

        var snapshot = _handler.GetDiagnostics();
        var meanBatchSize = snapshot.HandlerCalls == 0 ? 0 : (double)snapshot.ProcessedRequests / snapshot.HandlerCalls;
        var fullBatchFraction = snapshot.HandlerCalls == 0 ? 0 : (double)snapshot.FullBatches / snapshot.HandlerCalls;
        var meanHandlerMilliseconds = snapshot.HandlerCalls == 0
            ? 0
            : snapshot.HandlerTicks * 1_000.0 / Stopwatch.Frequency / snapshot.HandlerCalls;
        var meanWaveMilliseconds = snapshot.WaveCount == 0
            ? 0
            : snapshot.WaveTicks * 1_000.0 / Stopwatch.Frequency / snapshot.WaveCount;

        Console.WriteLine(
            "PARTITION_DIAGNOSTICS_JSON " + JsonSerializer.Serialize(new
            {
                Workload,
                Partitions,
                Method = _diagnosticMethod,
                WaveCount = snapshot.WaveCount,
                ProcessedRequests = snapshot.ProcessedRequests,
                HandlerCalls = snapshot.HandlerCalls,
                QueriedItems = snapshot.QueriedItems,
                FullBatches = snapshot.FullBatches,
                BatchSizeHistogram = snapshot.BatchSizes,
                MeanBatchSize = meanBatchSize,
                FullBatchFraction = fullBatchFraction,
                PeakConcurrency = snapshot.PeakConcurrency,
                MeanHandlerMs = meanHandlerMilliseconds,
                MeanWaveMs = meanWaveMilliseconds,
                ExactlyOnce = snapshot.ExactlyOnce,
                ResponsesValidated = snapshot.ResponsesValidated,
            }));

        if (!snapshot.ExactlyOnce || !snapshot.ResponsesValidated || snapshot.WaveCount != snapshot.ValidatedWaves)
        {
            throw new InvalidOperationException("The benchmark did not validate every completed wave and response.");
        }
    }

    private async Task RunWaveAsync(string method, bool explicitSubmission)
    {
        _diagnosticMethod = method;
        var waveStarted = Stopwatch.GetTimestamp();
        _handler.BeginWave(_requests.Length);

        var producers = new Task[ProducerCount];
        for (var producer = 0; producer < producers.Length; producer++)
        {
            var producerRequests = _producerRequests[producer];
            var producerGroups = _producerGroups[producer];
            producers[producer] = explicitSubmission
                ? Task.Run(() => SubmitExplicitAsync(producerGroups))
                : Task.Run(() => SubmitSinglesAsync(producerRequests));
        }

        // Keep a failed or stalled benchmark wave bounded; normal waves complete well below this limit.
        await Task.WhenAll(producers).WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        _handler.ValidateWave(_requests.Length);
        _handler.EndWave(waveStarted);
    }

    private async Task SubmitSinglesAsync(ProductRequest[] requests)
    {
        var pending = new Task<int>[requests.Length];
        for (var index = 0; index < requests.Length; index++)
        {
            var request = requests[index];
            pending[index] = _batcher.ExecuteAsync(request);
        }

        var responses = await Task.WhenAll(pending).ConfigureAwait(false);
        for (var index = 0; index < requests.Length; index++)
        {
            ValidateResponse(requests[index], responses[index]);
        }

        _handler.RecordResponsesValidated(responses.Length);
    }

    private async Task SubmitExplicitAsync(ProductRequest[][] groups)
    {
        var pending = new Task<IReadOnlyList<int>>[groups.Length];
        for (var group = 0; group < groups.Length; group++)
        {
            pending[group] = _batcher.ExecuteAsync(groups[group]);
        }

        var responses = await Task.WhenAll(pending).ConfigureAwait(false);
        var responseCount = 0;
        for (var group = 0; group < responses.Length; group++)
        {
            if (responses[group].Count != groups[group].Length)
            {
                throw new InvalidOperationException(
                    $"Explicit group {group} returned {responses[group].Count} responses; expected {groups[group].Length}.");
            }

            for (var index = 0; index < responses[group].Count; index++)
            {
                ValidateResponse(groups[group][index], responses[group][index]);
            }

            responseCount += responses[group].Count;
        }

        if (responseCount != groups.Sum(static group => group.Length))
        {
            throw new InvalidOperationException(
                $"Explicit submissions returned {responseCount} responses; expected {groups.Sum(static group => group.Length)}.");
        }

        _handler.RecordResponsesValidated(responseCount);
    }

    private static void ValidateResponse(ProductRequest request, int response)
    {
        var expected = unchecked((request.ProductId * 397) ^ request.RequestId);
        if (response != expected)
        {
            throw new InvalidOperationException(
                $"Request {request.RequestId} returned {response}; expected {expected}.");
        }
    }

    private static ProductRequest[] CreateRequests(string workload)
    {
        var count = workload == "SmallBurst" ? SmallBurstCount : BacklogCount;
        var requests = new ProductRequest[count];
        for (var index = 0; index < requests.Length; index++)
        {
            var productId = workload == "RepeatedKeys" ? index % RepeatedKeyCount : index;
            requests[index] = new ProductRequest(index, productId);
        }

        if (workload == "RepeatedKeys")
        {
            var random = new Random(1729);
            for (var index = requests.Length - 1; index > 0; index--)
            {
                var swap = random.Next(index + 1);
                (requests[index], requests[swap]) = (requests[swap], requests[index]);
            }
        }

        return requests;
    }

    private static ProductRequest[][] SplitAcrossProducers(ProductRequest[] requests)
    {
        var result = new ProductRequest[ProducerCount][];
        for (var producer = 0; producer < result.Length; producer++)
        {
            var start = producer * requests.Length / ProducerCount;
            var end = (producer + 1) * requests.Length / ProducerCount;
            result[producer] = requests[start..end];
        }

        return result;
    }

    private static ProductRequest[][] SplitIntoGroups(ProductRequest[] requests)
    {
        var groups = new ProductRequest[(requests.Length + 31) / 32][];
        for (var index = 0; index < groups.Length; index++)
        {
            var start = index * 32;
            var count = Math.Min(32, requests.Length - start);
            groups[index] = requests[start..(start + count)];
        }

        return groups;
    }

    private sealed record ProductRequest(int RequestId, int ProductId);

    private sealed class ProbeHandler : IRequestBatchHandler<ProductRequest, int>
    {
        private string _workload = null!;
        private readonly int[] _seen = new int[BacklogCount];
        private readonly int[] _batchSizes = new int[BatchSize + 1];
        private int _active;
        private int _peakConcurrency;
        private long _handlerCalls;
        private long _processedRequests;
        private long _queriedItems;
        private long _handlerTicks;
        private long _waveTicks;
        private long _waveCount;
        private long _validatedWaves;
        private long _validatedResponses;
        private long _fullBatches;

        public void Configure(string workload) => _workload = workload;

        public void BeginWave(int requestCount)
        {
            if (Volatile.Read(ref _active) != 0)
            {
                throw new InvalidOperationException("A previous benchmark wave is still active.");
            }

            Interlocked.Increment(ref _waveCount);
            Array.Clear(_seen, 0, requestCount);
        }

        public void RecordResponsesValidated(int count) => Interlocked.Add(ref _validatedResponses, count);

        public async ValueTask HandleAsync(
            IReadOnlyList<RequestBatchEntry<ProductRequest, int>> entries,
            CancellationToken cancellationToken = default)
        {
            if (entries.Count is < 1 or > BatchSize)
            {
                throw new InvalidOperationException($"Invalid Handler batch size {entries.Count}.");
            }

            var active = Interlocked.Increment(ref _active);
            UpdatePeak(active);
            var started = Stopwatch.GetTimestamp();
            Interlocked.Increment(ref _handlerCalls);
            Interlocked.Add(ref _processedRequests, entries.Count);
            Interlocked.Increment(ref _batchSizes[entries.Count]);
            if (entries.Count == BatchSize)
            {
                Interlocked.Increment(ref _fullBatches);
            }

            var distinctProducts = new HashSet<int>();
            for (var index = 0; index < entries.Count; index++)
            {
                distinctProducts.Add(entries[index].Request.ProductId);
            }

            Interlocked.Add(ref _queriedItems, distinctProducts.Count);
            try
            {
                var delay = _workload == "RepeatedKeys"
                    ? 8 + ((distinctProducts.Count + 7) / 8)
                    : 12;
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);

                for (var index = 0; index < entries.Count; index++)
                {
                    var request = entries[index].Request;
                    entries[index].SetResponse(unchecked((request.ProductId * 397) ^ request.RequestId));
                    Interlocked.Increment(ref _seen[request.RequestId]);
                }
            }
            finally
            {
                Interlocked.Add(ref _handlerTicks, Stopwatch.GetTimestamp() - started);
                Interlocked.Decrement(ref _active);
            }
        }

        public void ValidateWave(int requestCount)
        {
            if (Volatile.Read(ref _active) != 0 || Volatile.Read(ref _peakConcurrency) > MaxConcurrency)
            {
                throw new InvalidOperationException("Handler concurrency exceeded the configured limit.");
            }

            for (var index = 0; index < requestCount; index++)
            {
                if (Volatile.Read(ref _seen[index]) != 1)
                {
                    throw new InvalidOperationException(
                        $"Request {index} was processed {Volatile.Read(ref _seen[index])} times; expected once.");
                }
            }
        }

        public void EndWave(long waveStarted)
        {
            Interlocked.Increment(ref _validatedWaves);
            Interlocked.Add(ref _waveTicks, Stopwatch.GetTimestamp() - waveStarted);
        }

        public DiagnosticsSnapshot GetDiagnostics()
        {
            var calls = Volatile.Read(ref _handlerCalls);
            var processed = Volatile.Read(ref _processedRequests);
            return new DiagnosticsSnapshot(
                Volatile.Read(ref _waveCount),
                processed,
                calls,
                Volatile.Read(ref _queriedItems),
                Volatile.Read(ref _fullBatches),
                Volatile.Read(ref _peakConcurrency),
                Volatile.Read(ref _handlerTicks),
                Volatile.Read(ref _waveTicks),
                _batchSizes.ToArray(),
                Volatile.Read(ref _validatedWaves) == Volatile.Read(ref _waveCount),
                Volatile.Read(ref _validatedWaves),
                Volatile.Read(ref _validatedResponses) == processed);
        }

        private void UpdatePeak(int active)
        {
            var observed = Volatile.Read(ref _peakConcurrency);
            while (active > observed)
            {
                var previous = Interlocked.CompareExchange(ref _peakConcurrency, active, observed);
                if (previous == observed)
                {
                    return;
                }

                observed = previous;
            }
        }
    }

    private readonly record struct DiagnosticsSnapshot(
        long WaveCount,
        long ProcessedRequests,
        long HandlerCalls,
        long QueriedItems,
        long FullBatches,
        int PeakConcurrency,
        long HandlerTicks,
        long WaveTicks,
        int[] BatchSizes,
        bool ExactlyOnce,
        long ValidatedWaves,
        bool ResponsesValidated);
}
