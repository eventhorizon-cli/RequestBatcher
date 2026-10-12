using BufferQueue;
using BufferQueue.Memory;
using Microsoft.Extensions.DependencyInjection;
using RequestBatcher.PendingRequests;

namespace RequestBatcher.Tests.PendingRequests;

public sealed class QueueRoutingTests
{
    private const string TopicName = "request-batcher-routing-tests";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StringPartitionKey_ProducesCommonPrefixKeys_DistributesAcrossPartitions(bool useBatch)
    {
        const int partitionCount = 8;
        const int itemCount = 1_000;
        var items = Enumerable.Range(0, itemCount)
            .Select(index => CreatePendingRequest(new RoutingRequest($"user:{index:D4}")))
            .ToArray();

        await using var provider = CreateProvider<RoutingRequest>(
            topic => RequestBatchPartitionKey<RoutingRequest>
                .Create(static request => request.Key, partitionCount)
                .Configure(topic),
            partitionCount,
            batchSize: 128);
        var queue = provider.GetRequiredService<IBufferQueue>();
        var producer = queue.GetProducer<PendingBatchRequest<RoutingRequest>>(TopicName);

        if (useBatch)
        {
            await producer.ProduceAsync(items.AsMemory());
        }
        else
        {
            foreach (var item in items)
            {
                await producer.ProduceAsync(item);
            }
        }

        var counts = await ConsumeAllPartitionsAsync<RoutingRequest>(queue, itemCount, partitionCount);

        Assert.Equal(itemCount, counts.Sum());
        Assert.All(counts, count => Assert.InRange(count, 50, 200));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StringPartitionKey_ProducesRepeatedKey_RoutesToOnePartition(bool useBatch)
    {
        const int partitionCount = 8;
        const int itemCount = 64;

        await using var provider = CreateProvider<RoutingRequest>(
            topic => RequestBatchPartitionKey<RoutingRequest>
                .Create(static request => request.Key, partitionCount)
                .Configure(topic),
            partitionCount,
            batchSize: 16);
        var queue = provider.GetRequiredService<IBufferQueue>();
        var producer = queue.GetProducer<PendingBatchRequest<RoutingRequest>>(TopicName);
        var items = Enumerable.Range(0, itemCount)
            .Select(_ => CreatePendingRequest(new RoutingRequest("same-key")))
            .ToArray();

        if (useBatch)
        {
            await producer.ProduceAsync(items.AsMemory());
        }
        else
        {
            foreach (var item in items)
            {
                await producer.ProduceAsync(item);
            }
        }

        var counts = await ConsumeAllPartitionsAsync<RoutingRequest>(queue, itemCount, partitionCount);

        Assert.Equal(itemCount, counts.Sum());
        Assert.Equal(1, counts.Count(count => count != 0));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NumericPartitionKey_SingleConsumer_AlternatesHotPartitionsWithFortyEach(
        bool autoCommit)
    {
        const int partitionCount = 8;
        const int itemsPerPartition = 100;
        const int itemsToConsume = 80;

        await using var provider = CreateProvider<int>(
            topic => RequestBatchPartitionKey<int>
                .Create(static request => request, partitionCount)
                .Configure(topic),
            partitionCount,
            batchSize: 1);
        var queue = provider.GetRequiredService<IBufferQueue>();
        var producer = queue.GetProducer<PendingBatchRequest<int>>(TopicName);
        var items = Enumerable.Repeat(1, itemsPerPartition)
            .Concat(Enumerable.Repeat(8, itemsPerPartition))
            .Select(CreatePendingRequest)
            .ToArray();
        await producer.ProduceAsync(items.AsMemory());

        var consumer = queue.CreatePullConsumers<PendingBatchRequest<int>>(
                new BufferPullConsumerOptions
                {
                    TopicName = TopicName,
                    GroupName = $"routing-{Guid.NewGuid():N}",
                    BatchSize = 1,
                    AutoCommit = autoCommit,
                },
                1)
            .Single();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var consumed = new List<int>(itemsToConsume);

        await foreach (var batch in consumer.ConsumeAsync(cancellation.Token))
        {
            consumed.Add(batch.Single().Request);
            if (!autoCommit)
            {
                await consumer.CommitAsync();
            }

            if (consumed.Count == itemsToConsume)
            {
                break;
            }
        }

        Assert.Equal(Enumerable.Range(0, itemsToConsume).Select(index => index % 2 == 0 ? 1 : 8), consumed);
        Assert.Equal(40, consumed.Count(request => request == 1));
        Assert.Equal(40, consumed.Count(request => request == 8));
    }

    private static ServiceProvider CreateProvider<TRequest>(
        Action<MemoryBufferQueueOptions<PendingBatchRequest<TRequest>>> configurePartitionKey,
        int partitionCount,
        int batchSize)
    {
        var services = new ServiceCollection();
        services.AddBufferQueue(queue => queue.UseMemory(memory =>
            memory.AddTopic<PendingBatchRequest<TRequest>>(topic =>
            {
                topic.TopicName = TopicName;
                topic.PartitionNumber = partitionCount;
                topic.SegmentSize = Math.Max(16, batchSize);
                configurePartitionKey(topic);
            })));

        return services.BuildServiceProvider();
    }

    private static async Task<int[]> ConsumeAllPartitionsAsync<TRequest>(
        IBufferQueue queue,
        int expectedCount,
        int partitionCount)
    {
        var consumers = queue.CreatePullConsumers<PendingBatchRequest<TRequest>>(
                new BufferPullConsumerOptions
                {
                    TopicName = TopicName,
                    GroupName = $"routing-{Guid.NewGuid():N}",
                    BatchSize = 128,
                    AutoCommit = true,
                },
                partitionCount)
            .ToArray();
        var counts = new int[partitionCount];
        var consumedCount = 0;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var tasks = consumers
            .Select((consumer, partition) => ConsumePartitionAsync(
                consumer,
                partition,
                counts,
                expectedCount,
                cancellation,
                () => Volatile.Read(ref consumedCount),
                count => Interlocked.Add(ref consumedCount, count)))
            .ToArray();
        await Task.WhenAll(tasks);

        return counts;
    }

    private static async Task ConsumePartitionAsync<TRequest>(
        IBufferPullConsumer<PendingBatchRequest<TRequest>> consumer,
        int partition,
        int[] counts,
        int expectedCount,
        CancellationTokenSource cancellation,
        Func<int> getConsumedCount,
        Action<int> addConsumedCount)
    {
        try
        {
            await foreach (var batch in consumer.ConsumeAsync(cancellation.Token))
            {
                var count = batch.Count();
                counts[partition] += count;
                addConsumedCount(count);
                if (getConsumedCount() == expectedCount)
                {
                    cancellation.Cancel();
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
    }

    private static PendingBatchRequest<TRequest> CreatePendingRequest<TRequest>(TRequest request) =>
        new(request, default, static _ => { });

    private readonly record struct RoutingRequest(string Key);
}
