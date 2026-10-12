using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Engines;
using BenchmarkDotNet.Order;
using RequestBatcher.PendingRequests;

namespace RequestBatcher.Benchmarks.InMemory;

/// <summary>
/// Measures the request completion primitives used by single and explicit submissions.
/// </summary>
[MemoryDiagnoser]
[RankColumn]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
[SimpleJob(
    RunStrategy.Throughput,
    launchCount: 1,
    warmupCount: 3,
    iterationCount: 8,
    id: "RequestCompletion")]
public class RequestCompletionBenchmarks
{
    private static readonly Exception _exception = new InvalidOperationException("benchmark failure");

    [Params(128, 1_024)]
    public int RequestCount { get; set; }

    [Benchmark(Baseline = true, Description = "Single request success")]
    public void SingleRequestSuccess()
    {
        for (var index = 0; index < RequestCount; index++)
        {
            var pendingRequest = CreatePendingRequest();
            pendingRequest.TryStartProcessing();
            pendingRequest.CompleteSuccessfully();
        }
    }

    [Benchmark(Description = "Explicit batch shared success")]
    public void ExplicitBatchSharedSuccess()
    {
        var completion = new BatchSubmissionCompletion(RequestCount, default);
        for (var index = 0; index < RequestCount; index++)
        {
            var pendingRequest = CreatePendingRequest(completion);
            pendingRequest.TryStartProcessing();
            pendingRequest.CompleteSuccessfully();
        }
    }

    [Benchmark(Description = "Single request failure")]
    public void SingleRequestFailure()
    {
        for (var index = 0; index < RequestCount; index++)
        {
            var pendingRequest = CreatePendingRequest();
            pendingRequest.TryStartProcessing();
            pendingRequest.CompleteWithError(_exception);
        }
    }

    [Benchmark(Description = "Explicit batch shared failure")]
    public void ExplicitBatchSharedFailure()
    {
        var completion = new BatchSubmissionCompletion(RequestCount, default);
        for (var index = 0; index < RequestCount; index++)
        {
            var pendingRequest = CreatePendingRequest(completion);
            pendingRequest.TryStartProcessing();
            pendingRequest.CompleteWithError(_exception);
        }
    }

    [Benchmark(Description = "Single request cancellation")]
    public void SingleRequestCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var pendingRequests = new PendingBatchRequest<int>[RequestCount];
        for (var index = 0; index < RequestCount; index++)
        {
            pendingRequests[index] = new PendingBatchRequest<int>(
                index,
                cancellation.Token,
                static _ => { });
        }

        cancellation.Cancel();
        foreach (var pendingRequest in pendingRequests)
        {
            pendingRequest.FinishCanceledRequest();
        }
    }

    private static PendingBatchRequest<int> CreatePendingRequest(BatchSubmissionCompletion? completion = null) =>
        new(0, default, static _ => { }, completion);
}
