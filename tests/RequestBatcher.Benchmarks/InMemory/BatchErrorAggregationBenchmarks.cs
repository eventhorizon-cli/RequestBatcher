using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Engines;
using BenchmarkDotNet.Order;
using RequestBatcher.PendingRequests;

namespace RequestBatcher.Benchmarks.InMemory;

/// <summary>
/// Measures error accounting for shared explicit-batch completion.
/// </summary>
[MemoryDiagnoser]
[RankColumn]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
[SimpleJob(
    RunStrategy.Throughput,
    launchCount: 2,
    warmupCount: 3,
    iterationCount: 8,
    id: "BatchErrorAggregation")]
[IterationTime(150)]
public class BatchErrorAggregationBenchmarks
{
    [Params(10, 1_000)]
    public int GroupSize { get; set; }

    private Exception _repeatedException = null!;
    private Exception[] _repeatedExceptions = [];
    private Exception[] _distinctExceptions = [];
    private Exception[] _alternatingExceptions = [];
    private BatchSubmissionCompletion? _lastCompletion;
    private Exception[]? _lastExpectedExceptions;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _repeatedException = new BenchmarkBatchException();
        _repeatedExceptions = Enumerable.Repeat(_repeatedException, GroupSize).ToArray();
        _distinctExceptions = Enumerable.Range(0, GroupSize)
            .Select(static _ => (Exception)new BenchmarkBatchException())
            .ToArray();

        var first = new BenchmarkBatchException();
        var second = new BenchmarkBatchException();
        _alternatingExceptions = Enumerable.Range(0, GroupSize)
            .Select(index => index % 2 == 0 ? first : second)
            .ToArray();
    }

    [Benchmark(Baseline = true, Description = "Success")]
    public Task Success()
    {
        var completion = StartCompletion(expectedExceptions: null);
        for (var index = 0; index < GroupSize; index++)
        {
            completion.CompleteSuccessfully();
        }

        return completion.Task;
    }

    [Benchmark(Description = "Repeated same exception")]
    public Task RepeatedSameException()
    {
        var completion = StartCompletion(_repeatedExceptions);
        Complete(completion, _repeatedExceptions);
        return ObserveFailure(completion);
    }

    [Benchmark(Description = "Distinct exception references")]
    public Task DistinctExceptionReferences()
    {
        var completion = StartCompletion(_distinctExceptions);
        Complete(completion, _distinctExceptions);
        return ObserveFailure(completion);
    }

    [Benchmark(Description = "Alternating exception references")]
    public Task AlternatingExceptionReferences()
    {
        var completion = StartCompletion(_alternatingExceptions);
        Complete(completion, _alternatingExceptions);
        return ObserveFailure(completion);
    }

    [Benchmark(Description = "Concurrent repeated same exception")]
    public Task ConcurrentRepeatedSameException()
    {
        var completion = StartCompletion(_repeatedExceptions);
        CompleteConcurrently(completion, _repeatedExceptions);
        return ObserveFailure(completion);
    }

    [Benchmark(Description = "Concurrent distinct exception references")]
    public Task ConcurrentDistinctExceptionReferences()
    {
        var completion = StartCompletion(_distinctExceptions);
        CompleteConcurrently(completion, _distinctExceptions);
        return ObserveFailure(completion);
    }

    [GlobalCleanup]
    public void ValidateCompletion()
    {
        var completion = _lastCompletion ??
            throw new InvalidOperationException("The benchmark did not create a completion.");

        if (_lastExpectedExceptions is null)
        {
            if (!completion.Task.IsCompletedSuccessfully)
            {
                throw new InvalidOperationException("The success completion did not complete successfully.");
            }
        }
        else
        {
            ValidateFailure(completion.Task, _lastExpectedExceptions);
        }

        _lastCompletion = null;
        _lastExpectedExceptions = null;
    }

    private BatchSubmissionCompletion StartCompletion(Exception[]? expectedExceptions)
    {
        var completion = new BatchSubmissionCompletion(GroupSize, default);
        _lastCompletion = completion;
        _lastExpectedExceptions = expectedExceptions;
        return completion;
    }

    private static void Complete(BatchSubmissionCompletion completion, Exception[] exceptions)
    {
        foreach (var exception in exceptions)
        {
            completion.CompleteWithError(exception);
        }
    }

    private static void CompleteConcurrently(
        BatchSubmissionCompletion completion,
        Exception[] exceptions)
    {
        Parallel.For(
            0,
            exceptions.Length,
            index => completion.CompleteWithError(exceptions[index]));
    }

    private static Task ObserveFailure(BatchSubmissionCompletion completion)
    {
        // Observe without rethrowing shared exception instances and growing their stack traces across iterations.
        if (completion.Task.IsFaulted && completion.Task.Exception is not null)
        {
            return Task.CompletedTask;
        }

        throw new InvalidOperationException("The failure completion unexpectedly succeeded.");
    }

    private static void ValidateFailure(Task completion, Exception[] expectedExceptions)
    {
        if (!completion.IsFaulted || completion.Exception is not { } aggregate)
        {
            throw new InvalidOperationException("The failure completion did not fault.");
        }

        var actualExceptions = new HashSet<Exception>(
            aggregate.InnerExceptions,
            ReferenceEqualityComparer.Instance);
        var expectedDistinctExceptions = new HashSet<Exception>(
            expectedExceptions,
            ReferenceEqualityComparer.Instance);

        if (actualExceptions.Count != expectedDistinctExceptions.Count ||
            actualExceptions.Count != aggregate.InnerExceptions.Count ||
            !actualExceptions.SetEquals(expectedDistinctExceptions))
        {
            throw new InvalidOperationException(
                $"The completion aggregated {actualExceptions.Count} exception references; " +
                $"expected {expectedDistinctExceptions.Count}.");
        }
    }

    private sealed class BenchmarkBatchException : Exception;
}
