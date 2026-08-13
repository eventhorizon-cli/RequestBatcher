using StackExchange.Redis;

namespace RequestBatcher.Benchmarks.Redis;

internal sealed class RedisReadRequest(RedisKey key, RedisValue expectedValue)
{
    public RedisKey Key { get; } = key;

    public RedisValue ExpectedValue { get; } = expectedValue;

    public RedisValue Result { get; set; } = RedisValue.Null;
}
