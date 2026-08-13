namespace RequestBatcher.Benchmarks.PostgreSql;

internal readonly record struct InsertRequest(int RequestId, string Payload);
