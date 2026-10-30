using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using Microsoft.Extensions.DependencyInjection;
using RequestBatcher.Benchmarks.Infrastructure;

namespace RequestBatcher.Benchmarks.InMemory;

/// <summary>
/// Compares batch-local deduplication, caller aggregation, and handler fan-out under burst traffic.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Monitoring, launchCount: 1, warmupCount: 2, iterationCount: 8, invocationCount: 1)]
public class DeduplicationConcurrencyBenchmarks
{
    private const int RequestCount = 16_384;
    private const int DownstreamConcurrency = 32;
    private ServiceProvider _provider = null!;
    private IRequestBatcher<ReadRequest, int> _batcher = null!;
    private Workload _workload = null!;
    private ReadRequest[] _requests = [];
    private ReadRequest[][] _groups = [];
    private IReadOnlyList<int>[] _results = [];
    private int _querySize;

    /// <summary>Gets or sets the maximum number of original requests in a handler batch.</summary>
    [Params(32, 256)]
    public int BatchSize { get; set; }

    /// <summary>Gets or sets the maximum number of concurrent handler batches.</summary>
    [Params(4, 32)]
    public int MaxConcurrency { get; set; }

    /// <summary>Gets or sets the number of distinct keys in the fixed input population.</summary>
    [Params(32, RequestCount)]
    public int UniqueKeyCount { get; set; }

    /// <summary>Gets or sets the caller submission size, independently of handler batch size.</summary>
    [Params(1, 32, RequestCount)]
    public int SubmissionSize { get; set; }

    /// <summary>Creates a batcher that reads all distinct keys with one downstream call per batch.</summary>
    [GlobalSetup(Target = nameof(BulkRead))]
    public Task SetupBulkAsync() => SetupAsync(querySize: 0);

    /// <summary>Creates a batcher that joins individual distinct-key reads inside each handler.</summary>
    [GlobalSetup(Target = nameof(PerKeyWhenAll))]
    public Task SetupFanOutAsync() => SetupAsync(querySize: 1);

    /// <summary>Creates a batcher that joins reads of up to 32 distinct keys inside each handler.</summary>
    [GlobalSetup(Target = nameof(ChunkedWhenAll))]
    public Task SetupChunkedAsync() => SetupAsync(querySize: 32);

    /// <summary>Clears validation state outside the measured region.</summary>
    [IterationSetup]
    public void Reset()
    {
        _results = [];
        _workload.Reset();
    }

    /// <summary>Measures caller submission, batch deduplication, and one bulk read per handler.</summary>
    [Benchmark(Baseline = true, OperationsPerInvoke = RequestCount)]
    public Task BulkRead() => SubmitAsync();

    /// <summary>Measures caller submission and Task.WhenAll fan-out after batch-local deduplication.</summary>
    [Benchmark(OperationsPerInvoke = RequestCount)]
    public Task PerKeyWhenAll() => SubmitAsync();

    /// <summary>Measures deduplication before splitting distinct keys into concurrent chunks of 32.</summary>
    [Benchmark(OperationsPerInvoke = RequestCount)]
    public Task ChunkedWhenAll() => SubmitAsync();

    /// <summary>Checks every result and reports actual work outside the timed region.</summary>
    [IterationCleanup]
    public void Validate()
    {
        var index = 0;
        foreach (var group in _results)
        {
            foreach (var result in group)
            {
                if (index >= RequestCount || result != ValueFor(_requests[index].Key))
                {
                    throw new InvalidOperationException($"Incorrect response at input index {index}.");
                }

                index++;
            }
        }

        var snapshot = _workload.Batches.Snapshot;
        if (index != RequestCount || snapshot.RequestCount != RequestCount ||
            snapshot.MaximumBatchSize > BatchSize || _workload.ActiveHandlers != 0 ||
            _workload.ActiveCalls != 0 || _workload.PeakHandlers > MaxConcurrency ||
            _workload.PeakCalls > DownstreamConcurrency ||
            _workload.DistinctKeys < UniqueKeyCount || _workload.DistinctKeys > RequestCount ||
            _workload.ReadKeys != _workload.DistinctKeys ||
            _workload.Calls != _workload.ExpectedCalls)
        {
            throw new InvalidOperationException("Invalid result count, work count, or concurrency limit.");
        }

        for (var request = 0; request < RequestCount; request++)
        {
            if (_workload.Processed[request] != 1)
            {
                throw new InvalidOperationException($"Request {request} was not processed exactly once.");
            }
        }

        Console.WriteLine(
            $"Dedup validation: Strategy={(_querySize == 0 ? "BulkRead" : _querySize == 1 ? "PerKeyWhenAll" : "ChunkedWhenAll")}, " +
            $"BatchSize={BatchSize}, MaxConcurrency={MaxConcurrency}, UniqueKeyCount={UniqueKeyCount}, " +
            $"SubmissionSize={SubmissionSize}, HandlerBatches={snapshot.BatchCount}, " +
            $"MeanBatchSize={(double)RequestCount / snapshot.BatchCount:F2}, " +
            $"DistinctKeysRead={_workload.ReadKeys}, DedupRatio={1.0 - (double)_workload.ReadKeys / RequestCount:P2}, " +
            $"DownstreamCalls={_workload.Calls}, PeakHandlers={_workload.PeakHandlers}, " +
            $"PeakDownstream={_workload.PeakCalls}");
    }

    /// <summary>Drains and disposes the batcher outside measurement.</summary>
    [GlobalCleanup]
    public async Task CleanupAsync() => await _provider.DisposeAsync().ConfigureAwait(false);

    private async Task SetupAsync(int querySize)
    {
        _querySize = querySize;
        var keys = Enumerable.Range(0, RequestCount).Select(index => index % UniqueKeyCount).ToArray();
        new Random(1729).Shuffle(keys);
        _requests = keys.Select((key, index) => new ReadRequest(index, key)).ToArray();
        _groups = _requests.Chunk(SubmissionSize).ToArray();
        _workload = new Workload(querySize);

        var services = new ServiceCollection();
        services.AddSingleton(_ => _workload);
        services.AddRequestBatcher<ReadRequest, int, Handler>(ServiceLifetime.Singleton, options =>
        {
            options.BatchSize = BatchSize;
            options.MaxConcurrency = MaxConcurrency;
            options.MaxPendingRequests = RequestCount;
        });
        _provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
        _batcher = _provider.GetRequiredService<IRequestBatcher<ReadRequest, int>>();
        Reset();
        await SubmitAsync().ConfigureAwait(false);
        Validate();
    }

    private async Task SubmitAsync()
    {
        if (SubmissionSize == 1)
        {
            var tasks = new Task<int>[RequestCount];
            for (var index = 0; index < tasks.Length; index++)
            {
                tasks[index] = _batcher.ExecuteAsync(_requests[index]);
            }

            _results = [await Task.WhenAll(tasks).ConfigureAwait(false)];
        }
        else if (SubmissionSize == RequestCount)
        {
            _results = [await _batcher.ExecuteAsync(_requests).ConfigureAwait(false)];
        }
        else
        {
            var tasks = new Task<IReadOnlyList<int>>[_groups.Length];
            for (var index = 0; index < tasks.Length; index++)
            {
                tasks[index] = _batcher.ExecuteAsync(_groups[index]);
            }

            _results = await Task.WhenAll(tasks).ConfigureAwait(false);
        }
    }

    private static int ValueFor(int key) => key * 17 + 11;

    private readonly record struct ReadRequest(int Id, int Key);

    private sealed class Handler(Workload workload) : IRequestBatchHandler<ReadRequest, int>
    {
        public async ValueTask HandleAsync(
            IReadOnlyList<RequestBatchEntry<ReadRequest, int>> entries,
            CancellationToken cancellationToken = default)
        {
            Workload.UpdatePeak(ref workload.PeakHandlers, Interlocked.Increment(ref workload.ActiveHandlers));
            try
            {
                workload.Batches.Record(entries.Count);
                var keys = entries.Select(static entry => entry.Request.Key).Distinct().ToArray();
                Interlocked.Add(ref workload.DistinctKeys, keys.Length);
                Interlocked.Add(ref workload.ExpectedCalls,
                    workload.QuerySize == 0 ? 1 : (keys.Length + workload.QuerySize - 1) / workload.QuerySize);
                var values = workload.QuerySize switch
                {
                    0 => await workload.ReadBulkAsync(keys, cancellationToken).ConfigureAwait(false),
                    1 => await ReadIndividuallyAsync(keys, cancellationToken).ConfigureAwait(false),
                    _ => await ReadInChunksAsync(keys, cancellationToken).ConfigureAwait(false),
                };
                var byKey = new Dictionary<int, int>(keys.Length);
                for (var index = 0; index < keys.Length; index++)
                {
                    byKey.Add(keys[index], values[index]);
                }

                foreach (var entry in entries)
                {
                    Interlocked.Increment(ref workload.Processed[entry.Request.Id]);
                    entry.SetResponse(byKey[entry.Request.Key]);
                }
            }
            finally
            {
                Interlocked.Decrement(ref workload.ActiveHandlers);
            }
        }

        private async Task<int[]> ReadInChunksAsync(int[] keys, CancellationToken cancellationToken)
        {
            var reads = keys.Chunk(workload.QuerySize)
                .Select(chunk => workload.ReadBulkAsync(chunk, cancellationToken))
                .ToArray();
            var results = await Task.WhenAll(reads).ConfigureAwait(false);
            return results.SelectMany(static values => values).ToArray();
        }

        private async Task<int[]> ReadIndividuallyAsync(int[] keys, CancellationToken cancellationToken)
        {
            var reads = new Task<int>[keys.Length];
            for (var index = 0; index < keys.Length; index++)
            {
                reads[index] = workload.ReadSingleAsync(keys[index], cancellationToken);
            }

            return await Task.WhenAll(reads).ConfigureAwait(false);
        }
    }

    private sealed class Workload(int querySize) : IDisposable
    {
        private readonly SemaphoreSlim _slots = new(DownstreamConcurrency);
        public int QuerySize { get; } = querySize;
        public BatchExecutionCounter Batches { get; } = new();
        public int[] Processed { get; } = new int[RequestCount];
        public int DistinctKeys;
        public int ReadKeys;
        public int Calls;
        public int ExpectedCalls;
        public int ActiveCalls;
        public int PeakCalls;
        public int ActiveHandlers;
        public int PeakHandlers;

        public void Reset()
        {
            Array.Clear(Processed);
            Batches.Reset();
            DistinctKeys = ReadKeys = Calls = ExpectedCalls = ActiveCalls = PeakCalls = ActiveHandlers = PeakHandlers = 0;
        }

        public async Task<int[]> ReadBulkAsync(int[] keys, CancellationToken cancellationToken)
        {
            await _slots.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                RecordCall(keys.Length);
                await Task.Delay(1, cancellationToken).ConfigureAwait(false);
                return keys.Select(ValueFor).ToArray();
            }
            finally
            {
                Interlocked.Decrement(ref ActiveCalls);
                _slots.Release();
            }
        }

        public async Task<int> ReadSingleAsync(int key, CancellationToken cancellationToken)
        {
            await _slots.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                RecordCall(1);
                await Task.Delay(1, cancellationToken).ConfigureAwait(false);
                return ValueFor(key);
            }
            finally
            {
                Interlocked.Decrement(ref ActiveCalls);
                _slots.Release();
            }
        }

        public void Dispose() => _slots.Dispose();

        public static void UpdatePeak(ref int peak, int current)
        {
            var observed = Volatile.Read(ref peak);
            while (current > observed)
            {
                var previous = Interlocked.CompareExchange(ref peak, current, observed);
                if (previous == observed)
                {
                    return;
                }

                observed = previous;
            }
        }

        private void RecordCall(int keyCount)
        {
            Interlocked.Increment(ref Calls);
            Interlocked.Add(ref ReadKeys, keyCount);
            UpdatePeak(ref PeakCalls, Interlocked.Increment(ref ActiveCalls));
        }
    }
}
