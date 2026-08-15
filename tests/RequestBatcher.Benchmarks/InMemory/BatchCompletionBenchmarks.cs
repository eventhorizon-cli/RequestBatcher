using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Engines;
using BenchmarkDotNet.Order;
using RequestBatcher.Internal;

namespace RequestBatcher.Benchmarks.InMemory;

[MemoryDiagnoser]
[RankColumn]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
[SimpleJob(
    RunStrategy.Throughput,
    launchCount: 1,
    warmupCount: 3,
    iterationCount: 8,
    id: "InMemory")]
public class BatchCompletionBenchmarks
{
    [Params(128, 1_024, 8_192)]
    public int RequestCount { get; set; }

    [Benchmark(Baseline = true, Description = "Per-request TCS + Task.WhenAll")]
    public Task PerRequestTaskWhenAll()
    {
        var sources = new TaskCompletionSource[RequestCount];
        var tasks = new Task[RequestCount];
        for (var i = 0; i < RequestCount; i++)
        {
            var source = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            sources[i] = source;
            tasks[i] = source.Task;
        }

        var completion = Task.WhenAll(tasks);
        foreach (var source in sources)
        {
            source.SetResult();
        }

        return completion;
    }

    [Benchmark(Description = "Shared batch completion")]
    public Task SharedBatchCompletion()
    {
        var completion = new BatchSubmissionCompletion(RequestCount, default);
        for (var i = 0; i < RequestCount; i++)
        {
            completion.CompleteSuccessfully();
        }

        return completion.Task;
    }
}
