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
    id: "PostgreSQL")]
public class PostgreSqlPriceQueryBenchmarks
{
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
    private int _workloadExecuted;

    [Params(1_000)]
    public int RequestCount { get; set; }

    [Params(10)]
    public int DistinctProductCount { get; set; }

    [Params(100)]
    public int BatchSize { get; set; }

    [Params(4)]
    public int MaxConcurrency { get; set; }

    private NpgsqlDataSource DataSource =>
        _database?.DataSource ?? throw new InvalidOperationException("The benchmark has not been initialized.");

    private IRequestBatcher<PriceQueryRequest> RequestBatcher =>
        _requestBatcher ?? throw new InvalidOperationException("The benchmark has not been initialized.");

    [GlobalSetup]
    public async Task GlobalSetupAsync()
    {
        _database = await PostgreSqlPriceQueryBenchmarkDatabase
            .StartAsync(MaxConcurrency, DistinctProductCount)
            .ConfigureAwait(false);

        _requests = Enumerable.Range(0, RequestCount)
            .Select(index => new PriceQueryRequest(index % DistinctProductCount + 1))
            .ToArray();

        var services = new ServiceCollection();
        services.AddSingleton(DataSource);
        services.AddSingleton(_startGate);
        services.AddSingleton(_executionCounter);
        services.AddRequestBatcher<PriceQueryRequest, PostgreSqlPriceQueryBatchHandler>(
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

    [Benchmark(Baseline = true, Description = "Direct: one SELECT per request")]
    public async Task DirectSingleReadsAsync()
    {
        Volatile.Write(ref _workloadExecuted, 1);

        await Parallel.ForEachAsync(
            _requests,
            new ParallelOptions { MaxDegreeOfParallelism = MaxConcurrency },
            async (request, cancellationToken) =>
            {
                await ReadSingleAsync(request, cancellationToken).ConfigureAwait(false);
            }).ConfigureAwait(false);
    }

    [Benchmark(Description = "RequestBatcher: single-request submissions")]
    public async Task RequestBatcherSingleReadsAsync()
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

    [IterationCleanup(Target = nameof(RequestBatcherSingleReadsAsync))]
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

        if (snapshot.BatchCount >= RequestCount || snapshot.MaximumBatchSize <= 1)
        {
            throw new InvalidOperationException(
                $"Read batching did not occur: {snapshot.BatchCount} batches, " +
                $"maximum size {snapshot.MaximumBatchSize}.");
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

            if (_database is not null)
            {
                await _database.DisposeAsync().ConfigureAwait(false);
                _database = null;
            }
        }
    }

    private async Task ReadSingleAsync(
        PriceQueryRequest request,
        CancellationToken cancellationToken)
    {
        await using var command = DataSource.CreateCommand(SelectSingleSql);
        command.Parameters.AddWithValue("product_id", NpgsqlDbType.Bigint, request.ProductId);

        var price = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        request.Result = price is decimal decimalPrice
            ? decimalPrice
            : throw new InvalidOperationException($"Product {request.ProductId} was not found.");
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
