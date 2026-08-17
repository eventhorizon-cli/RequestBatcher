using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Engines;
using BenchmarkDotNet.Order;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;
using RequestBatcher.Benchmarks.Infrastructure;
using Testcontainers.PostgreSql;

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
public class PostgreSqlWriteBenchmarks
{
    private const string PostgreSqlImage = "postgres:17.6-alpine";
    private const string Payload = "RequestBatcher PostgreSQL benchmark payload";
    private const string InsertSingleSql = """
        INSERT INTO benchmark_writes (request_id, payload)
        VALUES (@request_id, @payload);
        """;
    private const string CreateTableSql = """
        CREATE TABLE benchmark_writes
        (
            request_id integer NOT NULL,
            payload text NOT NULL
        );
        """;

    private readonly BatchStartGate _startGate = new();
    private readonly BatchExecutionCounter _executionCounter = new();
    private InsertRequest[] _requests = [];
    private PostgreSqlContainer? _container;
    private NpgsqlDataSource? _dataSource;
    private ServiceProvider? _serviceProvider;
    private IRequestBatcher<InsertRequest>? _requestBatcher;
    private int _workloadExecuted;

    [Params(1_000)]
    public int RequestCount { get; set; }

    [Params(100)]
    public int BatchSize { get; set; }

    [Params(4)]
    public int MaxConcurrency { get; set; }

    private NpgsqlDataSource DataSource =>
        _dataSource ?? throw new InvalidOperationException("The benchmark has not been initialized.");

    private IRequestBatcher<InsertRequest> RequestBatcher =>
        _requestBatcher ?? throw new InvalidOperationException("The benchmark has not been initialized.");

    [GlobalSetup]
    public async Task GlobalSetupAsync()
    {
        _container = new PostgreSqlBuilder(PostgreSqlImage)
            .WithDatabase("requestbatcher_benchmarks")
            .WithUsername("postgres")
            .WithPassword("postgres")
            .Build();
        await _container.StartAsync().ConfigureAwait(false);

        var connectionString = new NpgsqlConnectionStringBuilder(_container.GetConnectionString())
        {
            MinPoolSize = MaxConcurrency,
            MaxPoolSize = MaxConcurrency,
            ApplicationName = "RequestBatcher.Benchmarks",
        };
        _dataSource = new NpgsqlDataSourceBuilder(connectionString.ConnectionString).Build();

        await CreateSchemaAsync().ConfigureAwait(false);
        await WarmConnectionPoolAsync().ConfigureAwait(false);

        _requests = Enumerable.Range(0, RequestCount)
            .Select(requestId => new InsertRequest(requestId, Payload))
            .ToArray();

        var services = new ServiceCollection();
        services.AddRequestBatcher<InsertRequest>(
            new PostgreSqlInsertBatchHandler(DataSource, _startGate, _executionCounter).HandleAsync,
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
        _requestBatcher = _serviceProvider.GetRequiredService<IRequestBatcher<InsertRequest>>();
    }

    [IterationSetup]
    public void IterationSetup()
    {
        using var command = DataSource.CreateCommand("TRUNCATE TABLE benchmark_writes;");
        command.ExecuteNonQuery();

        _startGate.Reset();
        _executionCounter.Reset();
        Volatile.Write(ref _workloadExecuted, 0);
    }

    [Benchmark(Baseline = true, Description = "Direct: one INSERT per request")]
    public async Task DirectSingleWritesAsync()
    {
        Volatile.Write(ref _workloadExecuted, 1);

        var writes = new Task[_requests.Length];
        try
        {
            for (var index = 0; index < _requests.Length; index++)
            {
                writes[index] = InsertSingleAsync(_requests[index]);
            }
        }
        finally
        {
            _startGate.Release();
        }

        await Task.WhenAll(writes).ConfigureAwait(false);
    }

    [Benchmark(Description = "RequestBatcher: single-request submissions")]
    public async Task RequestBatcherSingleWritesAsync()
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

    [Benchmark(Description = "RequestBatcher: one explicit batch submission")]
    public async Task RequestBatcherBatchWritesAsync()
    {
        Volatile.Write(ref _workloadExecuted, 1);

        Task processing;
        try
        {
            processing = RequestBatcher.ProcessAsync(_requests);
        }
        finally
        {
            _startGate.Release();
        }

        await processing.ConfigureAwait(false);
    }

    [IterationCleanup(Target = nameof(DirectSingleWritesAsync))]
    public void ValidateDirectWrites()
    {
        if (Volatile.Read(ref _workloadExecuted) != 0)
        {
            ValidateRowCount();
        }
    }

    [IterationCleanup(Targets = new[]
    {
        nameof(RequestBatcherSingleWritesAsync),
        nameof(RequestBatcherBatchWritesAsync),
    })]
    public void ValidateRequestBatcherWrites()
    {
        if (Volatile.Read(ref _workloadExecuted) == 0)
        {
            return;
        }

        ValidateRowCount();

        var snapshot = _executionCounter.Snapshot;
        if (snapshot.RequestCount != RequestCount)
        {
            throw new InvalidOperationException(
                $"The batch handler processed {snapshot.RequestCount} requests; expected {RequestCount}.");
        }

        if (snapshot.BatchCount >= RequestCount || snapshot.MaximumBatchSize <= 1)
        {
            throw new InvalidOperationException(
                $"Batching did not occur: {snapshot.BatchCount} batches, maximum size {snapshot.MaximumBatchSize}.");
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

            try
            {
                if (_dataSource is not null)
                {
                    await _dataSource.DisposeAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                _dataSource = null;

                if (_container is not null)
                {
                    await _container.DisposeAsync().ConfigureAwait(false);
                    _container = null;
                }
            }
        }
    }

    private async Task InsertSingleAsync(InsertRequest request)
    {
        await _startGate.WaitAsync().ConfigureAwait(false);

        await using var command = DataSource.CreateCommand(InsertSingleSql);
        command.Parameters.AddWithValue("request_id", NpgsqlDbType.Integer, request.RequestId);
        command.Parameters.AddWithValue("payload", NpgsqlDbType.Text, request.Payload);

        var affectedRows = await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        if (affectedRows != 1)
        {
            throw new InvalidOperationException(
                $"Expected to insert one row, but PostgreSQL reported {affectedRows}.");
        }
    }

    private async Task CreateSchemaAsync()
    {
        await using var command = DataSource.CreateCommand(CreateTableSql);
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private async Task WarmConnectionPoolAsync()
    {
        var connections = new List<NpgsqlConnection>(MaxConcurrency);
        try
        {
            for (var index = 0; index < MaxConcurrency; index++)
            {
                connections.Add(await DataSource.OpenConnectionAsync().ConfigureAwait(false));
            }
        }
        finally
        {
            foreach (var connection in connections)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private void ValidateRowCount()
    {
        using var command = DataSource.CreateCommand("SELECT COUNT(*) FROM benchmark_writes;");
        var actual = Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        if (actual != RequestCount)
        {
            throw new InvalidOperationException(
                $"PostgreSQL contains {actual} rows; expected {RequestCount}.");
        }
    }
}
