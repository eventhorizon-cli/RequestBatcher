using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Engines;
using BenchmarkDotNet.Order;

namespace RequestBatcher.Benchmarks.InMemory;

/// <summary>
/// Compares ordered response assignment with the response factory overload.
/// </summary>
/// <remarks>
/// Both methods construct fresh entries and validate every assigned response inside the measured operation. The
/// ordered-sequence control also constructs its response input inside the measured operation. Each invocation and
/// validation record represents one batch; reported time and allocation are per batch.
/// </remarks>
[MemoryDiagnoser]
[RankColumn]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
[SimpleJob(
    RunStrategy.Throughput,
    launchCount: 2,
    warmupCount: 3,
    iterationCount: 10,
    id: "ResponseAssignment")]
public class ResponseAssignmentBenchmarks
{
    private static readonly Func<int, int> _responseFactory = CreateResponse;
    private long _orderedSequenceValidatedBatchCount;
    private long _responseFactoryValidatedBatchCount;

    /// <summary>Gets or sets the number of entries in each response-assignment batch.</summary>
    [Params(32, 256)]
    public int BatchSize { get; set; }

    /// <summary>Resets successful validation counts before a benchmark case runs.</summary>
    [GlobalSetup]
    public void GlobalSetup()
    {
        _orderedSequenceValidatedBatchCount = 0;
        _responseFactoryValidatedBatchCount = 0;
    }

    /// <summary>Measures ordered response construction and sequence assignment for one batch.</summary>
    [Benchmark(Baseline = true, Description = "Ordered response sequence")]
    public int OrderedSequenceAssignment()
    {
        var entries = CreateEntries(BatchSize);
        var responses = new int[BatchSize];
        for (var index = 0; index < entries.Length; index++)
        {
            responses[index] = _responseFactory(entries[index].Request);
        }

        entries.SetResponses(responses);
        var checksum = ValidateResponses(entries);
        _orderedSequenceValidatedBatchCount++;
        return checksum;
    }

    /// <summary>Measures the response factory overload for one batch.</summary>
    [Benchmark(Description = "Response factory overload")]
    public int ResponseFactoryAssignment()
    {
        var entries = CreateEntries(BatchSize);
        entries.SetResponses(_responseFactory);
        var checksum = ValidateResponses(entries);
        _responseFactoryValidatedBatchCount++;
        return checksum;
    }

    /// <summary>Writes validation counts after the benchmark case completes.</summary>
    [GlobalCleanup]
    public void GlobalCleanup()
    {
        if (_orderedSequenceValidatedBatchCount == 0 && _responseFactoryValidatedBatchCount == 0)
        {
            throw new InvalidOperationException("No response-assignment batch completed response validation.");
        }

        if (_orderedSequenceValidatedBatchCount > 0)
        {
            WriteValidationRecord(nameof(OrderedSequenceAssignment), _orderedSequenceValidatedBatchCount);
        }

        if (_responseFactoryValidatedBatchCount > 0)
        {
            WriteValidationRecord(nameof(ResponseFactoryAssignment), _responseFactoryValidatedBatchCount);
        }
    }

    private static RequestBatchEntry<int, int>[] CreateEntries(int count)
    {
        var entries = new RequestBatchEntry<int, int>[count];
        for (var index = 0; index < count; index++)
        {
            entries[index] = new RequestBatchEntry<int, int>(index);
        }

        return entries;
    }

    private static int ValidateResponses(RequestBatchEntry<int, int>[] entries)
    {
        var checksum = 0;
        for (var index = 0; index < entries.Length; index++)
        {
            var entry = entries[index];
            var response = entry.GetResponse();
            if (response != CreateResponse(entry.Request))
            {
                throw new InvalidOperationException(
                    $"Entry {index} returned response {response}; expected {CreateResponse(entry.Request)}.");
            }

            checksum = unchecked(checksum + response);
        }

        return checksum;
    }

    private void WriteValidationRecord(string method, long validatedBatchCount) => Console.WriteLine(
        $"Response assignment validation: Method={method}, BatchSize={BatchSize}, " +
        $"ValidatedBatchCount={validatedBatchCount}, ResponseValues=True");

    private static int CreateResponse(int request) => request * 2 + 1;
}
