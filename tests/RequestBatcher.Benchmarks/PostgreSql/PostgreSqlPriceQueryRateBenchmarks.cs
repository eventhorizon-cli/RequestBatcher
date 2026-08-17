using System.Diagnostics;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Engines;
using BenchmarkDotNet.Order;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;
using RequestBatcher.Benchmarks.Infrastructure;

namespace RequestBatcher.Benchmarks.PostgreSql;

[MemoryDiagnoser]
[RankColumn]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
[SimpleJob(
    RunStrategy.Monitoring,
    launchCount: 1,
    warmupCount: 2,
    iterationCount: 8,
    invocationCount: 1,
    id: "PostgreSQL rate")]
public class PostgreSqlPriceQueryRateBenchmarks
{
    private const int MeasurementDurationSeconds = 3;
    private const int DistinctProductCount = 1_024;
    private const int SubmissionWindowMilliseconds = 10;
    private const string SelectSingleSql = """
        SELECT price
        FROM benchmark_product_prices
        WHERE product_id = @product_id;
        """;

    private readonly BatchStartGate _startGate = new();
    private readonly BatchExecutionCounter _executionCounter = new();
    private PriceQueryRequest[] _requests = [];
    private PostgreSqlPriceQueryBenchmarkDatabase? _database;
    private ServiceProvider? _serviceProvider;
    private IRequestBatcher<PriceQueryRequest>? _requestBatcher;
    private SemaphoreSlim? _directReadConcurrency;
    private int _workloadExecuted;

    [Params(100, 1_000, 5_000)]
    public int TargetQps { get; set; }

    [Params(1, 4, 16, 64)]
    public int MaxConcurrency { get; set; }

    [Params(100)]
    public int BatchSize { get; set; }

    private int RequestCount => checked(TargetQps * MeasurementDurationSeconds);

    private NpgsqlDataSource DataSource =>
        _database?.DataSource ?? throw new InvalidOperationException("The benchmark has not been initialized.");

    private IRequestBatcher<PriceQueryRequest> RequestBatcher =>
        _requestBatcher ?? throw new InvalidOperationException("The benchmark has not been initialized.");

    private SemaphoreSlim DirectReadConcurrency =>
        _directReadConcurrency ?? throw new InvalidOperationException("The benchmark has not been initialized.");

    [GlobalSetup]
    public async Task GlobalSetupAsync()
    {
        _database = await PostgreSqlPriceQueryBenchmarkDatabase
            .StartAsync(MaxConcurrency, DistinctProductCount)
            .ConfigureAwait(false);
        _requests = Enumerable.Range(0, RequestCount)
            .Select(index => new PriceQueryRequest(index % DistinctProductCount + 1))
            .ToArray();
        _directReadConcurrency = new SemaphoreSlim(MaxConcurrency, MaxConcurrency);

        var services = new ServiceCollection();
        services.AddRequestBatcher<PriceQueryRequest>(
            new PostgreSqlPriceQueryBatchHandler(DataSource, _startGate, _executionCounter).HandleAsync,
            ServiceLifetime.Singleton,
            options =>
            {
                options.BatchSize = BatchSize;
                options.MaxConcurrency = MaxConcurrency;
                options.MaxPendingRequests = RequestCount;
                options.UsePartitionKey(static request => request.ProductId);
            });

        _serviceProvider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
        _requestBatcher = _serviceProvider.GetRequiredService<IRequestBatcher<PriceQueryRequest>>();
    }

    [IterationSetup]
    public void IterationSetup()
    {
        foreach (var request in _requests)
        {
            request.Result = null;
        }

        _startGate.Reset();
        _executionCounter.Reset();
        Volatile.Write(ref _workloadExecuted, 0);
    }

    [Benchmark(Baseline = true, Description = "Direct: paced single SELECTs")]
    public async Task DirectPacedReadsAsync()
    {
        Volatile.Write(ref _workloadExecuted, 1);
        await SubmitAtTargetRateAsync(ReadSingleAsync).ConfigureAwait(false);
    }

    [Benchmark(Description = "RequestBatcher: paced single-request submissions")]
    public async Task RequestBatcherPacedReadsAsync()
    {
        Volatile.Write(ref _workloadExecuted, 1);
        _startGate.Release();
        await SubmitAtTargetRateAsync(request => RequestBatcher.ProcessAsync(request)).ConfigureAwait(false);
    }

    [IterationCleanup(Target = nameof(DirectPacedReadsAsync))]
    public void ValidateDirectReads()
    {
        if (Volatile.Read(ref _workloadExecuted) != 0)
        {
            ValidateResults();
        }
    }

    [IterationCleanup(Target = nameof(RequestBatcherPacedReadsAsync))]
    public void ValidateRequestBatcherReads()
    {
        if (Volatile.Read(ref _workloadExecuted) == 0)
        {
            return;
        }

        ValidateResults();

        var snapshot = _executionCounter.Snapshot;
        if (snapshot.RequestCount != RequestCount)
        {
            throw new InvalidOperationException(
                $"The batch handler processed {snapshot.RequestCount} reads; expected {RequestCount}.");
        }
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
            _directReadConcurrency?.Dispose();
            _directReadConcurrency = null;

            if (_database is not null)
            {
                await _database.DisposeAsync().ConfigureAwait(false);
                _database = null;
            }
        }
    }

    private async Task SubmitAtTargetRateAsync(Func<PriceQueryRequest, Task> submit)
    {
        var reads = new Task[_requests.Length];
        var submittedCount = 0;
        var startTimestamp = Stopwatch.GetTimestamp();

        while (submittedCount < _requests.Length)
        {
            var elapsed = Stopwatch.GetElapsedTime(startTimestamp);
            var targetSubmissionCount = Math.Min(
                _requests.Length,
                (int)(elapsed.TotalSeconds * TargetQps));

            while (submittedCount < targetSubmissionCount)
            {
                reads[submittedCount] = submit(_requests[submittedCount]);
                submittedCount++;
            }

            if (submittedCount < _requests.Length)
            {
                await Task.Delay(SubmissionWindowMilliseconds).ConfigureAwait(false);
            }
        }

        await Task.WhenAll(reads).ConfigureAwait(false);
    }

    private async Task ReadSingleAsync(PriceQueryRequest request)
    {
        await DirectReadConcurrency.WaitAsync().ConfigureAwait(false);
        try
        {
            await using var command = DataSource.CreateCommand(SelectSingleSql);
            command.Parameters.AddWithValue("product_id", NpgsqlDbType.Bigint, request.ProductId);

            var price = await command.ExecuteScalarAsync().ConfigureAwait(false);
            request.Result = price is decimal decimalPrice
                ? decimalPrice
                : throw new InvalidOperationException($"Product {request.ProductId} was not found.");
        }
        finally
        {
            DirectReadConcurrency.Release();
        }
    }

    private void ValidateResults()
    {
        foreach (var request in _requests)
        {
            if (request.Result != (decimal)request.ProductId)
            {
                throw new InvalidOperationException(
                    $"Product {request.ProductId} returned an unexpected price.");
            }
        }
    }
}
