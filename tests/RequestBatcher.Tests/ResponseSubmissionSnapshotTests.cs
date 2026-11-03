using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace RequestBatcher.Tests;

public sealed class ResponseSubmissionSnapshotTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_RejectedBeforeMaterialization_DoesNotEnumerate(bool stopped)
    {
        var handler = new Mock<IRequestBatchHandler<int, int>>(MockBehavior.Strict);
        await using var provider = CreateProvider(handler);
        var batcher = provider.GetRequiredService<IRequestBatcher<int, int>>();
        var input = new Mock<IEnumerable<int>>(MockBehavior.Strict);
        using var cancellation = new CancellationTokenSource();
        if (stopped)
        {
            await provider.DisposeAsync();
        }
        else
        {
            cancellation.Cancel();
        }

        var submission = batcher.ExecuteAsync(input.Object, cancellation.Token);
        if (stopped)
        {
            await Assert.ThrowsAsync<ObjectDisposedException>(() => submission);
        }
        else
        {
            var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => submission);
            Assert.Equal(cancellation.Token, exception.CancellationToken);
            Assert.True(submission.IsCanceled);
        }

        input.VerifyNoOtherCalls();
        handler.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ExecuteAsync_StreamingInput_EnumeratesOnceAndPreservesResponseOrder()
    {
        var handled = new ConcurrentQueue<int>();
        var handler = CreateHandler(handled);
        await using var provider = CreateProvider(handler);
        var batcher = provider.GetRequiredService<IRequestBatcher<int, int>>();
        var enumerations = 0;

        IEnumerable<int> Input()
        {
            enumerations++;
            yield return 3;
            yield return 1;
            yield return 2;
        }

        var responses = await batcher.ExecuteAsync(Input()).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, enumerations);
        Assert.Equal(new[] { 54, 20, 37 }, responses);
        Assert.Equal(new[] { 1, 2, 3 }, handled.Order());
        handler.Verify(value => value.HandleAsync(It.IsAny<IReadOnlyList<RequestBatchEntry<int, int>>>(),
            It.IsAny<CancellationToken>()), Times.Exactly(3));
        handler.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ExecuteAsync_CanceledDuringMaterialization_RejectsCompleteBatchWithOriginalToken()
    {
        var handled = new ConcurrentQueue<int>();
        var handler = CreateHandler(handled);
        await using var provider = CreateProvider(handler);
        var batcher = provider.GetRequiredService<IRequestBatcher<int, int>>();
        using var cancellation = new CancellationTokenSource();

        IEnumerable<int> Input()
        {
            yield return 1;
            cancellation.Cancel();
            yield return 2;
        }

        var submission = batcher.ExecuteAsync(Input(), cancellation.Token);
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => submission);

        Assert.True(submission.IsCanceled);
        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Empty(handled);
        handler.VerifyNoOtherCalls();

        Assert.Equal(54, await batcher.ExecuteAsync(3).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(3, Assert.Single(handled));
        handler.Verify(value => value.HandleAsync(It.IsAny<IReadOnlyList<RequestBatchEntry<int, int>>>(),
            It.IsAny<CancellationToken>()), Times.Once);
        handler.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ExecuteAsync_StoppedDuringMaterialization_DoesNotAdmitPartialBatch()
    {
        var handler = new Mock<IRequestBatchHandler<int, int>>(MockBehavior.Strict);
        await using var provider = CreateProvider(handler);
        var batcher = provider.GetRequiredService<IRequestBatcher<int, int>>();
        Task? disposal = null;

        IEnumerable<int> Input()
        {
            yield return 1;
            disposal = provider.DisposeAsync().AsTask();
            yield return 2;
        }

        await Assert.ThrowsAsync<ObjectDisposedException>(() => batcher.ExecuteAsync(Input()));
        Assert.NotNull(disposal);
        await disposal.WaitAsync(TimeSpan.FromSeconds(5));

        handler.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_CallerMutatesInputAfterAdmission_UsesOwnedSnapshot(bool useList)
    {
        IList<int> input = useList ? new List<int> { 3, 1, 2 } : new[] { 3, 1, 2 };
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handled = new ConcurrentQueue<int>();
        var handler = new Mock<IRequestBatchHandler<int, int>>(MockBehavior.Strict);
        handler.Setup(value => value.HandleAsync(It.IsAny<IReadOnlyList<RequestBatchEntry<int, int>>>(),
                It.IsAny<CancellationToken>()))
            .Returns(async (IReadOnlyList<RequestBatchEntry<int, int>> entries, CancellationToken _) =>
            {
                started.TrySetResult();
                await release.Task;
                CompleteEntries(entries, handled);
            });
        await using var provider = CreateProvider(handler);
        var batcher = provider.GetRequiredService<IRequestBatcher<int, int>>();

        var submission = batcher.ExecuteAsync(input);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            input[0] = 100;
            input[1] = 200;
            input[2] = 300;
            if (useList)
            {
                input.Add(400);
            }
        }
        finally
        {
            release.TrySetResult();
        }

        Assert.Equal(new[] { 54, 20, 37 }, await submission.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(new[] { 1, 2, 3 }, handled.Order());
        handler.Verify(value => value.HandleAsync(It.IsAny<IReadOnlyList<RequestBatchEntry<int, int>>>(),
            It.IsAny<CancellationToken>()), Times.Exactly(3));
        handler.VerifyNoOtherCalls();
    }

    private static Mock<IRequestBatchHandler<int, int>> CreateHandler(ConcurrentQueue<int> handled)
    {
        var handler = new Mock<IRequestBatchHandler<int, int>>(MockBehavior.Strict);
        handler.Setup(value => value.HandleAsync(It.IsAny<IReadOnlyList<RequestBatchEntry<int, int>>>(),
                It.IsAny<CancellationToken>()))
            .Returns((IReadOnlyList<RequestBatchEntry<int, int>> entries, CancellationToken _) =>
            {
                CompleteEntries(entries, handled);
                return ValueTask.CompletedTask;
            });
        return handler;
    }

    private static void CompleteEntries(IReadOnlyList<RequestBatchEntry<int, int>> entries, ConcurrentQueue<int> handled)
    {
        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            handled.Enqueue(entry.Request);
            entry.SetResponse(entry.Request * 17 + 3);
        }
    }

    private static ServiceProvider CreateProvider(Mock<IRequestBatchHandler<int, int>> handler)
    {
        var services = new ServiceCollection();
        services.AddSingleton(handler);
        services.AddRequestBatcher<int, int, MockHandlerProxy>(ServiceLifetime.Singleton, options =>
        {
            options.BatchSize = 1;
            options.MaxConcurrency = 1;
        });
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    private sealed class MockHandlerProxy(Mock<IRequestBatchHandler<int, int>> handler) : IRequestBatchHandler<int, int>
    {
        public ValueTask HandleAsync(IReadOnlyList<RequestBatchEntry<int, int>> entries,
            CancellationToken cancellationToken = default) => handler.Object.HandleAsync(entries, cancellationToken);
    }
}
