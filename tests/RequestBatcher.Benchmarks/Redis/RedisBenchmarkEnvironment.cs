using StackExchange.Redis;
using Testcontainers.Redis;

namespace RequestBatcher.Benchmarks.Redis;

internal sealed class RedisBenchmarkEnvironment : IAsyncDisposable
{
    private const string DefaultRedisImage = "redis:7.4.5-alpine";
    private const string RedisImageEnvironmentVariable = "REQUEST_BATCHER_REDIS_IMAGE";
    private RedisContainer? _container;
    private ConnectionMultiplexer? _connection;

    public IDatabase Database =>
        _connection?.GetDatabase() ??
        throw new InvalidOperationException("The Redis benchmark environment has not been started.");

    public async Task StartAsync()
    {
        var configuredImage = Environment.GetEnvironmentVariable(RedisImageEnvironmentVariable);
        var image = string.IsNullOrWhiteSpace(configuredImage)
            ? DefaultRedisImage
            : configuredImage;

        _container = new RedisBuilder(image).Build();
        await _container.StartAsync().ConfigureAwait(false);

        try
        {
            _connection = await ConnectionMultiplexer
                .ConnectAsync(_container.GetConnectionString())
                .ConfigureAwait(false);
            await Database.PingAsync().ConfigureAwait(false);
        }
        catch
        {
            await _container.DisposeAsync().ConfigureAwait(false);
            _container = null;
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
            _connection = null;
        }

        if (_container is not null)
        {
            await _container.DisposeAsync().ConfigureAwait(false);
            _container = null;
        }
    }
}
