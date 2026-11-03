using System.Buffers;
using System.Runtime.CompilerServices;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Engines;
using BenchmarkDotNet.Order;

namespace RequestBatcher.Benchmarks.InMemory;

/// <summary>
/// Compares the public response factory assignment with a benchmark-local array-pool candidate.
/// </summary>
/// <typeparam name="TResponse">The response type used by the benchmark case.</typeparam>
/// <remarks>
/// Each invocation assigns one batch and includes fresh entry creation and full per-entry response validation, so
/// reported time and allocation are per batch. Both implementations cache the count for response filling, then read
/// the count once more and use that validated value for assignment. The local pool candidate computes
/// all responses before assigning any response and rejects duplicates during assignment, matching the public method's failure
/// behavior. It returns reference-containing buffers with clearing enabled. Run with <c>--unrollFactor 1</c>; the
/// job keeps invocation-count calibration automatic. The pooled method is a benchmark-local experiment, not a
/// production change.
/// </remarks>
[GenericTypeArguments(typeof(int))]
[GenericTypeArguments(typeof(string))]
[MemoryDiagnoser]
[RankColumn]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
[SimpleJob(
    RunStrategy.Throughput,
    launchCount: 2,
    warmupCount: 3,
    iterationCount: 8,
    id: "ResponseFactoryBuffer")]
public class ResponseFactoryBufferBenchmarks<TResponse>
{
    private const int MaximumBatchSize = 256;
    private static readonly EqualityComparer<TResponse> _responseComparer = EqualityComparer<TResponse>.Default;

    private TResponse[] _expectedResponses = null!;
    private Func<int, TResponse> _responseFactory = null!;
    private long _publicFactoryValidatedBatches;
    private long _pooledFactoryValidatedBatches;

    /// <summary>Gets or sets the number of request entries assigned in each batch.</summary>
    [Params(1, 32, 256)]
    public int BatchSize { get; set; }

    /// <summary>Precomputes response values and caches the response factory outside measurement.</summary>
    [GlobalSetup]
    public void GlobalSetup()
    {
        _expectedResponses = new TResponse[MaximumBatchSize];
        if (typeof(TResponse) == typeof(int))
        {
            for (var request = 0; request < MaximumBatchSize; request++)
            {
                _expectedResponses[request] = (TResponse)(object)(request * 17 + 3);
            }
        }
        else if (typeof(TResponse) == typeof(string))
        {
            for (var request = 0; request < MaximumBatchSize; request++)
            {
                _expectedResponses[request] = (TResponse)(object)$"response-{request}";
            }
        }
        else
        {
            throw new NotSupportedException($"Unsupported response type {typeof(TResponse)}.");
        }

        _responseFactory = GetPrecomputedResponse;
        _publicFactoryValidatedBatches = 0;
        _pooledFactoryValidatedBatches = 0;
    }

    /// <summary>Measures the public response factory overload for one batch.</summary>
    [Benchmark(Baseline = true, Description = "Public factory")]
    public int PublicFactoryAssignment()
    {
        var entries = CreateEntries(BatchSize);
        entries.SetResponses(_responseFactory);
        var checksum = ValidateResponses(entries);
        _publicFactoryValidatedBatches++;
        return checksum;
    }

    /// <summary>Measures a benchmark-local response factory that rents its temporary array.</summary>
    [Benchmark(Description = "ArrayPool candidate")]
    public int PooledFactoryAssignment()
    {
        var entries = CreateEntries(BatchSize);
        SetResponsesWithPool(entries, _responseFactory);
        var checksum = ValidateResponses(entries);
        _pooledFactoryValidatedBatches++;
        return checksum;
    }

    /// <summary>Prints successful validation counts after each benchmark case.</summary>
    [GlobalCleanup]
    public void GlobalCleanup()
    {
        if (_publicFactoryValidatedBatches == 0 && _pooledFactoryValidatedBatches == 0)
        {
            throw new InvalidOperationException("No response factory batch completed response validation.");
        }

        if (_publicFactoryValidatedBatches > 0)
        {
            WriteValidationRecord(nameof(PublicFactoryAssignment), _publicFactoryValidatedBatches);
        }

        if (_pooledFactoryValidatedBatches > 0)
        {
            WriteValidationRecord(nameof(PooledFactoryAssignment), _pooledFactoryValidatedBatches);
        }
    }

    private TResponse GetPrecomputedResponse(int request) => _expectedResponses[request];

    private static RequestBatchEntry<int, TResponse>[] CreateEntries(int count)
    {
        var entries = new RequestBatchEntry<int, TResponse>[count];
        for (var request = 0; request < count; request++)
        {
            entries[request] = new RequestBatchEntry<int, TResponse>(request);
        }

        return entries;
    }

    private static void SetResponsesWithPool(
        IReadOnlyList<RequestBatchEntry<int, TResponse>> entries,
        Func<int, TResponse> responseFactory)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(responseFactory);

        var count = entries.Count;
        var responses = ArrayPool<TResponse>.Shared.Rent(count);
        try
        {
            for (var index = 0; index < count; index++)
            {
                responses[index] = responseFactory(entries[index].Request);
            }

            var validatedCount = entries.Count;
            if (validatedCount != count)
            {
                throw new InvalidOperationException(
                    "The request entry count changed while the response factory was executing.");
            }

            for (var index = 0; index < validatedCount; index++)
            {
                entries[index].SetResponse(responses[index]);
            }
        }
        finally
        {
            ArrayPool<TResponse>.Shared.Return(
                responses,
                clearArray: RuntimeHelpers.IsReferenceOrContainsReferences<TResponse>());
        }
    }

    private int ValidateResponses(IReadOnlyList<RequestBatchEntry<int, TResponse>> entries)
    {
        if (entries.Count != BatchSize)
        {
            throw new InvalidOperationException($"Received {entries.Count} entries; expected {BatchSize}.");
        }

        var checksum = 0;
        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            if (entry.Request != index)
            {
                throw new InvalidOperationException(
                    $"Entry at index {index} had request {entry.Request}; expected {index}.");
            }

            var response = entry.GetResponse();
            if (!_responseComparer.Equals(response, _expectedResponses[entry.Request]))
            {
                throw new InvalidOperationException($"Entry {index} returned an unexpected response value.");
            }

            checksum = unchecked(checksum + index);
        }

        return checksum;
    }

    private void WriteValidationRecord(string method, long validatedBatches) => Console.WriteLine(
        $"Response factory buffer validation: Method={method}, ResponseType={typeof(TResponse).FullName}, " +
        $"BatchSize={BatchSize}, ValidatedBatches={validatedBatches}, ResponseValues=True");
}
