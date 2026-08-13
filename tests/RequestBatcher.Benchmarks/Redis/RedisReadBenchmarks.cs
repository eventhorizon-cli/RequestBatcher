using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Engines;
using BenchmarkDotNet.Order;
using Microsoft.Extensions.DependencyInjection;
using RequestBatcher.Benchmarks.Infrastructure;
using StackExchange.Redis;

namespace RequestBatcher.Benchmarks.Redis;

[MemoryDiagnoser]
[RankColumn]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
[SimpleJob(
    RunStrategy.Monitoring,
    launchCount: 1,
    warmupCount: 2,
    iterationCount: 8,
    invocationCount: 1,
    id: "Redis")]
public class RedisReadBenchmarks
{
    private const string ValuePrefix = "RequestBatcher Redis read payload ";
    private readonly BatchStartGate _startGate = new();
    private readonly BatchExecutionCounter _executionCounter = new();
    private RedisReadRequest[] _requests = [];
    private RedisBenchmarkEnvironment? _environment;
    private ServiceProvider? _serviceProvider;
    private IRequestBatcher<RedisReadRequest>? _requestBatcher;
    private int _workloadExecuted;

    [Params(1_000)]
    public int RequestCount { get; set; }

    [Params(100)]
    public int BatchSize { get; set; }

    [Params(4)]
    public int MaxConcurrency { get; set; }

    private IDatabase Database =>
        _environment?.Database ??
        throw new InvalidOperationException("The benchmark has not been initialized.");

    private IRequestBatcher<RedisReadRequest> RequestBatcher =>
        _requestBatcher ?? throw new InvalidOperationException("The benchmark has not been initialized.");

    [GlobalSetup]
    public async Task GlobalSetupAsync()
    {
        _environment = new RedisBenchmarkEnvironment();
        await _environment.StartAsync().ConfigureAwait(false);

        _requests = Enumerable.Range(0, RequestCount)
            .Select(requestId => new RedisReadRequest(
                $"requestbatcher:read:{requestId}",
                $"{ValuePrefix}{requestId}"))
            .ToArray();

        var seedEntries = _requests
            .Select(request => new KeyValuePair<RedisKey, RedisValue>(request.Key, request.ExpectedValue))
            .ToArray();
        if (!await Database.StringSetAsync(seedEntries).ConfigureAwait(false))
        {
            throw new InvalidOperationException("Redis rejected the read benchmark seed data.");
        }

        var services = new ServiceCollection();
        services.AddRequestBatcher<RedisReadRequest>(
            new RedisReadBatchHandler(Database, _startGate, _executionCounter).HandleAsync,
            ServiceLifetime.Singleton,
            options =>
            {
                options.BatchSize = BatchSize;
                options.MaxConcurrency = MaxConcurrency;
                options.MaxPendingRequests = RequestCount;
            });

        _serviceProvider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
        _requestBatcher = _serviceProvider.GetRequiredService<IRequestBatcher<RedisReadRequest>>();
    }

    [IterationSetup]
    public void IterationSetup()
    {
        foreach (var request in _requests)
        {
            request.Result = RedisValue.Null;
        }

        _startGate.Reset();
        _executionCounter.Reset();
        Volatile.Write(ref _workloadExecuted, 0);
    }

    [Benchmark(Baseline = true, Description = "Direct: one GET per request")]
    public async Task DirectSingleReadsAsync()
    {
        Volatile.Write(ref _workloadExecuted, 1);

        var reads = new Task[_requests.Length];
        try
        {
            for (var index = 0; index < _requests.Length; index++)
            {
                reads[index] = ReadSingleAsync(_requests[index]);
            }
        }
        finally
        {
            _startGate.Release();
        }

        await Task.WhenAll(reads).ConfigureAwait(false);
    }

    [Benchmark(Description = "RequestBatcher: one MGET per batch")]
    public async Task RequestBatcherReadsAsync()
    {
        Volatile.Write(ref _workloadExecuted, 1);

        var reads = new Task[_requests.Length];
        try
        {
            for (var index = 0; index < _requests.Length; index++)
            {
                reads[index] = RequestBatcher.ProcessAsync(_requests[index]);
            }
        }
        finally
        {
            _startGate.Release();
        }

        await Task.WhenAll(reads).ConfigureAwait(false);
    }

    [IterationCleanup(Target = nameof(DirectSingleReadsAsync))]
    public void ValidateDirectReads()
    {
        if (Volatile.Read(ref _workloadExecuted) != 0)
        {
            ValidateResults();
        }
    }

    [IterationCleanup(Target = nameof(RequestBatcherReadsAsync))]
    public void ValidateRequestBatcherReads()
    {
        if (Volatile.Read(ref _workloadExecuted) == 0)
        {
            return;
        }

        ValidateResults();
        ValidateBatching();
    }

    [GlobalCleanup]
    public async Task GlobalCleanupAsync()
    {
        try
        {
            if (_serviceProvider is not null)
            {
                await _serviceProvider.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _serviceProvider = null;
            _requestBatcher = null;

            if (_environment is not null)
            {
                await _environment.DisposeAsync().ConfigureAwait(false);
                _environment = null;
            }
        }
    }

    private async Task ReadSingleAsync(RedisReadRequest request)
    {
        await _startGate.WaitAsync().ConfigureAwait(false);
        request.Result = await Database.StringGetAsync(request.Key).ConfigureAwait(false);
    }

    private void ValidateResults()
    {
        foreach (var request in _requests)
        {
            if (request.Result != request.ExpectedValue)
            {
                throw new InvalidOperationException(
                    $"Redis key '{request.Key}' returned an unexpected value.");
            }
        }
    }

    private void ValidateBatching()
    {
        var snapshot = _executionCounter.Snapshot;
        if (snapshot.RequestCount != RequestCount)
        {
            throw new InvalidOperationException(
                $"The batch handler processed {snapshot.RequestCount} reads; expected {RequestCount}.");
        }

        if (snapshot.BatchCount >= RequestCount || snapshot.MaximumBatchSize <= 1)
        {
            throw new InvalidOperationException(
                $"Read batching did not occur: {snapshot.BatchCount} batches, " +
                $"maximum size {snapshot.MaximumBatchSize}.");
        }
    }
}
