using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;

namespace RequestBatcher.Tests.Batching;

public sealed class UnkeyedPartitionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProcessAsync_UnkeyedSubmission_CombinesAfterSlotsRelease(bool explicitBatch)
    {
        const int concurrency = 2;
        var entered = Enumerable.Range(0, concurrency)
            .Select(_ => NewSource())
            .ToArray();
        var release = NewSource();
        var batches = new ConcurrentQueue<int[]>();

        await using var provider = CreateProvider(
            async (requests, _) =>
            {
                if (requests[0] < 0)
                {
                    entered[-requests[0] - 1].SetResult();
                    await release.Task;
                    return;
                }

                batches.Enqueue(requests.ToArray());
            },
            options =>
            {
                options.BatchSize = 4;
                options.MaxConcurrency = concurrency;
            });
        var batcher = provider.GetRequiredService<IRequestBatcher<int>>();

        try
        {
            var blockers = new List<Task>(concurrency);
            for (var index = 0; index < concurrency; index++)
            {
                blockers.Add(batcher.ProcessAsync(-index - 1));
                await entered[index].Task.WaitAsync(TimeSpan.FromSeconds(5));
            }

            int[] targetRequests = explicitBatch ? [1, 2, 3, 4] : [];
            var target = explicitBatch
                ? batcher.ProcessAsync(targetRequests)
                : Task.WhenAll(Enumerable.Range(1, 4).Select(request => batcher.ProcessAsync(request)));

            release.SetResult();
            await Task.WhenAll(blockers.Append(target)).WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            release.TrySetResult();
        }

        Assert.Equal(new[] { 1, 2, 3, 4 }, Assert.Single(batches));
    }

    [Fact]
    public async Task ProcessAsync_UnkeyedResponseBatch_CombinesAfterSlotsRelease()
    {
        const int concurrency = 2;
        var entered = Enumerable.Range(0, concurrency)
            .Select(_ => NewSource())
            .ToArray();
        var release = NewSource();
        var batches = new ConcurrentQueue<int[]>();

        await using var provider = CreateResponseProvider(
            async (entries, _) =>
            {
                if (entries[0].Request < 0)
                {
                    entries.SetResponses(static _ => "blocked");
                    entered[-entries[0].Request - 1].SetResult();
                    await release.Task;
                    return;
                }

                batches.Enqueue(entries.Select(entry => entry.Request).ToArray());
                entries.SetResponses(request => $"response-{request}");
            },
            options =>
            {
                options.BatchSize = 4;
                options.MaxConcurrency = concurrency;
            });
        var batcher = provider.GetRequiredService<IRequestBatcher<int, string>>();

        try
        {
            var blockers = new List<Task<string>>(concurrency);
            for (var index = 0; index < concurrency; index++)
            {
                blockers.Add(batcher.ProcessAsync(-index - 1));
                await entered[index].Task.WaitAsync(TimeSpan.FromSeconds(5));
            }

            var target = batcher.ProcessAsync([1, 2, 3, 4]);

            release.SetResult();
            var responses = await target.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.WhenAll(blockers).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(new[] { "response-1", "response-2", "response-3", "response-4" }, responses);
        }
        finally
        {
            release.TrySetResult();
        }

        Assert.Equal(new[] { 1, 2, 3, 4 }, Assert.Single(batches));
    }

    private static ServiceProvider CreateProvider(
        Func<IReadOnlyList<int>, CancellationToken, ValueTask> handler,
        Action<RequestBatchOptions<int>> configure)
    {
        var services = new ServiceCollection();
        services.AddSingleton(handler);
        services.AddRequestBatcher<int, VoidHandler>(ServiceLifetime.Singleton, configure);
        return services.BuildServiceProvider();
    }

    private static ServiceProvider CreateResponseProvider(
        Func<IReadOnlyList<RequestBatchItem<int, string>>, CancellationToken, ValueTask> handler,
        Action<RequestBatchOptions<int>> configure)
    {
        var services = new ServiceCollection();
        services.AddSingleton(handler);
        services.AddRequestBatcher<int, string, ResponseHandler>(ServiceLifetime.Singleton, configure);
        return services.BuildServiceProvider();
    }

    private static TaskCompletionSource NewSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class VoidHandler(
        Func<IReadOnlyList<int>, CancellationToken, ValueTask> handler)
        : IRequestBatchHandler<int>
    {
        public ValueTask HandleAsync(
            IReadOnlyList<int> requests,
            CancellationToken cancellationToken = default) =>
            handler(requests, cancellationToken);
    }

    private sealed class ResponseHandler(
        Func<IReadOnlyList<RequestBatchItem<int, string>>, CancellationToken, ValueTask> handler)
        : IRequestBatchHandler<int, string>
    {
        public ValueTask HandleAsync(
            IReadOnlyList<RequestBatchItem<int, string>> entries,
            CancellationToken cancellationToken = default) =>
            handler(entries, cancellationToken);
    }
}
