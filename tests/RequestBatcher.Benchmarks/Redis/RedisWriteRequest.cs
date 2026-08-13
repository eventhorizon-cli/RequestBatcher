using StackExchange.Redis;

namespace RequestBatcher.Benchmarks.Redis;

internal readonly record struct RedisWriteRequest(RedisKey Key, RedisValue Value);
