using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Engines;
using BenchmarkDotNet.Order;
using BufferQueue;
using Microsoft.Extensions.DependencyInjection;

namespace RequestBatcher.Benchmarks.InMemory;

/// <summary>
/// Measures draining a prefilled BufferQueue pull consumer without producer, handler,
/// tracing, admission, or request-completion work in the timed region.
/// </summary>
[MemoryDiagnoser]
[RankColumn]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
[SimpleJob(
    RunStrategy.Throughput,
    launchCount: 2,
    warmupCount: 3,
    iterationCount: 8,
    invocationCount: 1,
    id: "BufferQueuePull")]
public class QueuePullBenchmarks
{
    private const int RequestCount = 4_096;
    private const string TopicName = "request-batcher-queue-pull-benchmark";

    private ServiceProvider? _provider;
    private IBufferProducer<QueueItem>? _producer;
    private IBufferPullConsumer<QueueItem>? _consumer;
    private QueueItem[]? _items;
    private int[]? _seen;
    private int[] _partitionItems = [];
    private int _consumedCount;
    private int _batchCount;
    private int _maximumBatchSize;
    private int _synchronousMoveNextCount;
    private int _validatedWaves;
    private CancellationTokenSource? _timeout;

    /// <summary>
    /// Controls the maximum number of items returned by one consumer pull.
    /// </summary>
    [Params(32, 128)]
    public int BatchSize { get; set; }

    /// <summary>
    /// Controls the number of memory topic partitions assigned to the one consumer.
    /// </summary>
    [Params(1, 16)]
    public int PartitionCount { get; set; }

    /// <summary>
    /// Creates the real in-memory topic and its single pull consumer outside measurement.
    /// </summary>
    [GlobalSetup]
    public void GlobalSetup()
    {
        var services = new ServiceCollection();
        services.AddBufferQueue(queue => queue.UseMemory(memory =>
            memory.AddTopic<QueueItem>(topic =>
            {
                topic.TopicName = TopicName;
                topic.PartitionNumber = PartitionCount;
                topic.SegmentSize = Math.Max(16, BatchSize);
                topic.UsePartitionKey(static item => item.Id);
            })));

        _provider = services.BuildServiceProvider();
        var queue = _provider.GetRequiredService<IBufferQueue>();
        _producer = queue.GetProducer<QueueItem>(TopicName);
        _consumer = queue.CreatePullConsumers<QueueItem>(
                new BufferPullConsumerOptions
                {
                    TopicName = TopicName,
                    GroupName = $"queue-pull-benchmark-{Guid.NewGuid():N}",
                    BatchSize = BatchSize,
                    AutoCommit = true,
                },
                1)
            .Single();
        _items = Enumerable.Range(0, RequestCount)
            .Select(static id => new QueueItem(id))
            .ToArray();
        _seen = new int[RequestCount];
        _partitionItems = new int[PartitionCount];
    }

    /// <summary>
    /// Refills the topic before each measured invocation, outside the timed region.
    /// </summary>
    [IterationSetup]
    public void IterationSetup()
    {
        Array.Clear(_seen!);
        Array.Clear(_partitionItems);
        _consumedCount = 0;
        _batchCount = 0;
        _maximumBatchSize = 0;
        _synchronousMoveNextCount = 0;
        _timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        _producer!.ProduceAsync(_items!.AsMemory()).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Drains all prefilled records by awaiting the consumer's ValueTask pull operation.
    /// </summary>
    [Benchmark(OperationsPerInvoke = RequestCount)]
    public async Task ConsumePrefilledQueueAsync()
    {
        await using var enumerator = _consumer!
            .ConsumeAsync(_timeout!.Token)
            .GetAsyncEnumerator(_timeout.Token);

        while (_consumedCount < RequestCount)
        {
            var moveNext = enumerator.MoveNextAsync();
            if (moveNext.IsCompletedSuccessfully)
            {
                _synchronousMoveNextCount++;
            }

            if (!await moveNext.ConfigureAwait(false))
            {
                throw new InvalidOperationException("The prefilled queue ended before all records were consumed.");
            }

            var batchSize = 0;
            var partitionResidue = -1;
            foreach (var item in enumerator.Current)
            {
                if ((uint)item.Id >= RequestCount)
                {
                    throw new InvalidOperationException($"Unexpected queue item id: {item.Id}.");
                }

                _seen![item.Id]++;
                if (batchSize == 0)
                {
                    partitionResidue = item.Id % PartitionCount;
                }
                _consumedCount++;
                batchSize++;
            }

            if (batchSize == 0 || batchSize > BatchSize)
            {
                throw new InvalidOperationException(
                    $"Observed invalid pull size {batchSize}; expected 1..{BatchSize}.");
            }

            _maximumBatchSize = Math.Max(_maximumBatchSize, batchSize);
            _partitionItems[partitionResidue] += batchSize;
            _batchCount++;
        }
    }

    /// <summary>
    /// Validates the result after measurement and reports sync completion separately from timed work.
    /// </summary>
    [IterationCleanup]
    public void IterationCleanup()
    {
        _timeout!.Dispose();
        if (_consumedCount != RequestCount || _seen!.Any(static count => count != 1)
            || _partitionItems.Any(count => count != RequestCount / PartitionCount))
        {
            throw new InvalidOperationException(
                $"Expected {RequestCount} unique records, consumed {_consumedCount}.");
        }

        if (_batchCount == 0 || _maximumBatchSize > BatchSize)
        {
            throw new InvalidOperationException(
                $"Observed invalid pull batches: count={_batchCount}, " +
                $"maximum size={_maximumBatchSize}, limit={BatchSize}.");
        }

        if (_synchronousMoveNextCount != _batchCount)
        {
            throw new InvalidOperationException(
                $"Expected every prefilled pull to complete synchronously, observed " +
                $"{_synchronousMoveNextCount}/{_batchCount} synchronous MoveNextAsync calls.");
        }
        _validatedWaves++;
    }

    /// <summary>
    /// Releases the in-memory queue after all benchmark launches for this parameter set finish.
    /// </summary>
    [GlobalCleanup]
    public void GlobalCleanup()
    {
        Console.WriteLine(
            $"QueuePull BatchSize={BatchSize}, Partitions={PartitionCount}, " +
            $"Batches={_batchCount}, MaxBatch={_maximumBatchSize}, " +
            $"SynchronousMoveNext={_synchronousMoveNextCount}/{_batchCount}, " +
            $"Requests={_consumedCount}/{RequestCount}, ValidatedWaves={_validatedWaves}");
        _provider?.Dispose();
    }

    private sealed record QueueItem(int Id);
}
