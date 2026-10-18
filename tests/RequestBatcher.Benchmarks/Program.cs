using BenchmarkDotNet.Running;
using RequestBatcher.Benchmarks.PostgreSql;
using RequestBatcher.Benchmarks.SchedulingEvaluation;

if (args.Length > 0 && args[0] == "--scheduling-probe")
{
    await SchedulingProbe.RunAsync(args[1..]);
    return;
}

BenchmarkSwitcher
    .FromAssembly(typeof(PostgreSqlWriteBenchmarks).Assembly)
    .Run(args);
