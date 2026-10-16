using Microsoft.Extensions.DependencyInjection;

namespace RequestBatcher.Tests;

public sealed class DelegateSubmissionValidationTests
{
    [Fact]
    public async Task ExecuteAsync_RequestSingleWithoutDelegateBeforeAndAfterBinding_ThrowsMissingHandler()
    {
        await using var provider = BuildRequestProvider();
        var batcher = provider.GetRequiredService<IRequestBatcher<int>>();

        AssertMissingHandler(() => batcher.ExecuteAsync(1));

        await batcher.ExecuteAsync(2, static (IReadOnlyList<int> _) => { });

        AssertMissingHandler(() => batcher.ExecuteAsync(3));
    }

    [Fact]
    public async Task ExecuteAsync_RequestListWithoutDelegateBeforeAndAfterBinding_ThrowsMissingHandler()
    {
        await using var provider = BuildRequestProvider();
        var batcher = provider.GetRequiredService<IRequestBatcher<int>>();

        AssertMissingHandler(() => batcher.ExecuteAsync([1, 2]));

        await batcher.ExecuteAsync([3, 4], static (IReadOnlyList<int> _) => { });

        AssertMissingHandler(() => batcher.ExecuteAsync([5, 6]));
    }

    [Fact]
    public async Task ExecuteAsync_ResponseSingleWithoutDelegateBeforeAndAfterBinding_ThrowsMissingHandler()
    {
        await using var provider = BuildResponseProvider();
        var batcher = provider.GetRequiredService<IRequestBatcher<int, int>>();

        AssertMissingHandler(() => batcher.ExecuteAsync(1));

        var response = await batcher.ExecuteAsync(
            2,
            static (IReadOnlyList<RequestBatchEntry<int, int>> entries) =>
                entries.SetResponses(static request => request));
        Assert.Equal(2, response);

        AssertMissingHandler(() => batcher.ExecuteAsync(3));
    }

    [Fact]
    public async Task ExecuteAsync_ResponseListWithoutDelegateBeforeAndAfterBinding_ThrowsMissingHandler()
    {
        await using var provider = BuildResponseProvider();
        var batcher = provider.GetRequiredService<IRequestBatcher<int, int>>();

        AssertMissingHandler(() => batcher.ExecuteAsync([1, 2]));

        var responses = await batcher.ExecuteAsync(
            [3, 4],
            static (IReadOnlyList<RequestBatchEntry<int, int>> entries) =>
                entries.SetResponses(static request => request));
        Assert.Equal([3, 4], responses);

        AssertMissingHandler(() => batcher.ExecuteAsync([5, 6]));
    }

    [Fact]
    public async Task ExecuteAsync_RequestListCanceledBeforeBinding_AllowsDifferentHandlerToBind()
    {
        await using var provider = BuildRequestProvider();
        var batcher = provider.GetRequiredService<IRequestBatcher<int>>();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var canceled = batcher.ExecuteAsync(
            [1, 2],
            static (IReadOnlyList<int> _) => { },
            cancellation.Token);

        Assert.True(canceled.IsCanceled);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);

        await batcher.ExecuteAsync([3, 4], static (IReadOnlyList<int> _) => { });
    }

    [Fact]
    public async Task ExecuteAsync_ResponseSingleCanceledBeforeBinding_AllowsDifferentHandlerToBind()
    {
        await using var provider = BuildResponseProvider();
        var batcher = provider.GetRequiredService<IRequestBatcher<int, int>>();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var canceled = batcher.ExecuteAsync(
            1,
            static (IReadOnlyList<RequestBatchEntry<int, int>> entries) =>
                entries.SetResponses(static _ => 10),
            cancellation.Token);

        Assert.True(canceled.IsCanceled);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);

        var response = await batcher.ExecuteAsync(
            2,
            static (IReadOnlyList<RequestBatchEntry<int, int>> entries) =>
                entries.SetResponses(static _ => 20));
        Assert.Equal(20, response);
    }

    [Fact]
    public async Task ExecuteAsync_ResponseListCanceledBeforeBinding_AllowsDifferentHandlerToBind()
    {
        await using var provider = BuildResponseProvider();
        var batcher = provider.GetRequiredService<IRequestBatcher<int, int>>();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var canceled = batcher.ExecuteAsync(
            [1, 2],
            static (IReadOnlyList<RequestBatchEntry<int, int>> entries) =>
                entries.SetResponses(static _ => 10),
            cancellation.Token);

        Assert.True(canceled.IsCanceled);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);

        var responses = await batcher.ExecuteAsync(
            [3, 4],
            static (IReadOnlyList<RequestBatchEntry<int, int>> entries) =>
                entries.SetResponses(static request => request * 10));
        Assert.Equal([30, 40], responses);
    }

    [Fact]
    public async Task ExecuteAsync_TypedRegistrationExplicitList_RejectsInlineHandler()
    {
        var services = new ServiceCollection();
        services.AddRequestBatcher<int, NoOpHandler>(ServiceLifetime.Singleton);
        await using var provider = services.BuildServiceProvider();
        var batcher = provider.GetRequiredService<IRequestBatcher<int>>();

        var exception = Assert.Throws<InvalidOperationException>(() =>
        {
            _ = batcher.ExecuteAsync([1, 2], static (IReadOnlyList<int> _) => { });
        });

        Assert.Contains("AddDelegateRequestBatcher", exception.Message, StringComparison.Ordinal);
    }

    private static ServiceProvider BuildRequestProvider()
    {
        var services = new ServiceCollection();
        services.AddDelegateRequestBatcher<int>();
        return services.BuildServiceProvider();
    }

    private static ServiceProvider BuildResponseProvider()
    {
        var services = new ServiceCollection();
        services.AddDelegateRequestBatcher<int, int>();
        return services.BuildServiceProvider();
    }

    private static void AssertMissingHandler(Action execute)
    {
        var exception = Assert.Throws<InvalidOperationException>(execute);
        Assert.Contains("delegate handler", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class NoOpHandler : IRequestBatchHandler<int>
    {
        public ValueTask HandleAsync(
            IReadOnlyList<int> requests,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
}
