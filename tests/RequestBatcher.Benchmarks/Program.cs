using BenchmarkDotNet.Running;
using RequestBatcher.Benchmarks.PostgreSql;

BenchmarkSwitcher
    .FromAssembly(typeof(PostgreSqlWriteBenchmarks).Assembly)
    .Run(args);
