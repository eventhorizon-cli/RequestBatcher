using Npgsql;
using Testcontainers.PostgreSql;

namespace RequestBatcher.Benchmarks.PostgreSql;

internal sealed class PostgreSqlPriceQueryBenchmarkDatabase : IAsyncDisposable
{
    private const string PostgreSqlImage = "postgres:17.6-alpine";
    private const string CreateTableSql = """
        CREATE TABLE benchmark_product_prices
        (
            product_id bigint PRIMARY KEY,
            price numeric(18, 2) NOT NULL
        );
        """;

    private readonly PostgreSqlContainer _container;

    private PostgreSqlPriceQueryBenchmarkDatabase(
        PostgreSqlContainer container,
        NpgsqlDataSource dataSource)
    {
        _container = container;
        DataSource = dataSource;
    }

    public NpgsqlDataSource DataSource { get; }

    public static async Task<PostgreSqlPriceQueryBenchmarkDatabase> StartAsync(
        int maxConcurrency,
        int distinctProductCount)
    {
        var container = new PostgreSqlBuilder(PostgreSqlImage)
            .WithDatabase("requestbatcher_benchmarks")
            .WithUsername("postgres")
            .WithPassword("postgres")
            .Build();
        await container.StartAsync().ConfigureAwait(false);

        var connectionString = new NpgsqlConnectionStringBuilder(container.GetConnectionString())
        {
            MinPoolSize = maxConcurrency,
            MaxPoolSize = maxConcurrency,
            ApplicationName = "RequestBatcher.Benchmarks",
        };
        var dataSource = new NpgsqlDataSourceBuilder(connectionString.ConnectionString).Build();

        try
        {
            await CreateSchemaAsync(dataSource, distinctProductCount).ConfigureAwait(false);
            await WarmConnectionPoolAsync(dataSource, maxConcurrency).ConfigureAwait(false);
            return new PostgreSqlPriceQueryBenchmarkDatabase(container, dataSource);
        }
        catch
        {
            try
            {
                await dataSource.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                await container.DisposeAsync().ConfigureAwait(false);
            }

            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await DataSource.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            await _container.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task CreateSchemaAsync(NpgsqlDataSource dataSource, int distinctProductCount)
    {
        await using var connection = await dataSource.OpenConnectionAsync().ConfigureAwait(false);
        await using var createTable = connection.CreateCommand();
        createTable.CommandText = CreateTableSql;
        await createTable.ExecuteNonQueryAsync().ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO benchmark_product_prices (product_id, price)
            SELECT product_id, product_id * 1.00
            FROM generate_series(1, $1::bigint) AS product_id;
            """;
        command.Parameters.AddWithValue(distinctProductCount);
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static async Task WarmConnectionPoolAsync(NpgsqlDataSource dataSource, int maxConcurrency)
    {
        var warmup = Enumerable.Range(0, maxConcurrency)
            .Select(_ => dataSource.OpenConnectionAsync().AsTask())
            .ToArray();
        var connections = await Task.WhenAll(warmup).ConfigureAwait(false);
        foreach (var connection in connections)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }
}
