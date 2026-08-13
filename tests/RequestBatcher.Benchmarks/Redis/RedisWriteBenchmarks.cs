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
public class RedisWriteBenchmarks
{
    private const string ValuePrefix = "RequestBatcher Redis write payload ";
    private readonly BatchStartGate _startGate = new();
    private readonly BatchExecutionCounter _executionCounter = new();
    private RedisWriteRequest[] _requests = [];
    private RedisKey[] _keys = [];
    private RedisBenchmarkEnvironment? _environment;
    private ServiceProvider? _serviceProvider;
    private IRequestBatcher<RedisWriteRequest>? _requestBatcher;
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

    private IRequestBatcher<RedisWriteRequest> RequestBatcher =>
        _requestBatcher ?? throw new InvalidOperationException("The benchmark has not been initialized.");

    [GlobalSetup]
    public async Task GlobalSetupAsync()
    {
        _environment = new RedisBenchmarkEnvironment();
        await _environment.StartAsync().ConfigureAwait(false);

        _requests = Enumerable.Range(0, RequestCount)
            .Select(requestId => new RedisWriteRequest(
                $"requestbatcher:write:{requestId}",
                $"{ValuePrefix}{requestId}"))
            .ToArray();
        _keys = _requests.Select(request => request.Key).ToArray();

        var services = new ServiceCollection();
        services.AddRequestBatcher<RedisWriteRequest>(
            new RedisWriteBatchHandler(Database, _startGate, _executionCounter).HandleAsync,
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
        _requestBatcher = _serviceProvider.GetRequiredService<IRequestBatcher<RedisWriteRequest>>();
    }

    [IterationSetup]
    public void IterationSetup()
    {
        Database.KeyDelete(_keys);
        _startGate.Reset();
        _executionCounter.Reset();
        Volatile.Write(ref _workloadExecuted, 0);
    }

    [Benchmark(Baseline = true, Description = "Direct: one SET per request")]
    public async Task DirectSingleWritesAsync()
    {
        Volatile.Write(ref _workloadExecuted, 1);

        var writes = new Task[_requests.Length];
        try
        {
            for (var index = 0; index < _requests.Length; index++)
            {
                writes[index] = WriteSingleAsync(_requests[index]);
            }
        }
        finally
        {
            _startGate.Release();
        }

        await Task.WhenAll(writes).ConfigureAwait(false);
    }

    [Benchmark(Description = "RequestBatcher: one MSET per batch")]
    public async Task RequestBatcherWritesAsync()
    {
        Volatile.Write(ref _workloadExecuted, 1);

        var writes = new Task[_requests.Length];
        try
        {
            for (var index = 0; index < _requests.Length; index++)
            {
                writes[index] = RequestBatcher.ProcessAsync(_requests[index]);
            }
        }
        finally
        {
            _startGate.Release();
        }

        await Task.WhenAll(writes).ConfigureAwait(false);
    }

    [IterationCleanup(Target = nameof(DirectSingleWritesAsync))]
    public void ValidateDirectWrites()
    {
        if (Volatile.Read(ref _workloadExecuted) != 0)
        {
            ValidateValues();
        }
    }

    [IterationCleanup(Target = nameof(RequestBatcherWritesAsync))]
    public void ValidateRequestBatcherWrites()
    {
        if (Volatile.Read(ref _workloadExecuted) == 0)
        {
            return;
        }

        ValidateValues();
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

    private async Task WriteSingleAsync(RedisWriteRequest request)
    {
        await _startGate.WaitAsync().ConfigureAwait(false);
        if (!await Database.StringSetAsync(request.Key, request.Value).ConfigureAwait(false))
        {
            throw new InvalidOperationException($"Redis rejected key '{request.Key}'.");
        }
    }

    private void ValidateValues()
    {
        var values = Database.StringGet(_keys);
        if (values.Length != _requests.Length)
        {
            throw new InvalidOperationException(
                $"Redis returned {values.Length} values; expected {_requests.Length}.");
        }

        for (var index = 0; index < values.Length; index++)
        {
            if (values[index] != _requests[index].Value)
            {
                throw new InvalidOperationException(
                    $"Redis key '{_requests[index].Key}' contains an unexpected value.");
            }
        }
    }

    private void ValidateBatching()
    {
        var snapshot = _executionCounter.Snapshot;
        if (snapshot.RequestCount != RequestCount)
        {
            throw new InvalidOperationException(
                $"The batch handler processed {snapshot.RequestCount} writes; expected {RequestCount}.");
        }

        if (snapshot.BatchCount >= RequestCount || snapshot.MaximumBatchSize <= 1)
        {
            throw new InvalidOperationException(
                $"Write batching did not occur: {snapshot.BatchCount} batches, " +
                $"maximum size {snapshot.MaximumBatchSize}.");
        }
    }
}
