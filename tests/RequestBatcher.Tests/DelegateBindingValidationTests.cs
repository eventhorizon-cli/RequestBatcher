using Microsoft.Extensions.DependencyInjection;

namespace RequestBatcher.Tests;

public sealed class DelegateBindingValidationTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void AddDelegateRequestBatcher_DuplicateDefault_LeavesServicesAndConfigurationUnchanged(
        bool existingResponse,
        bool duplicateResponse)
    {
        var services = new ServiceCollection();
        if (existingResponse)
        {
            services.AddDelegateRequestBatcher<int, int>();
        }
        else
        {
            services.AddDelegateRequestBatcher<int>();
        }

        var descriptors = services.ToArray();
        var configureCalled = false;

        var exception = Assert.Throws<InvalidOperationException>(() =>
        {
            if (duplicateResponse)
            {
                services.AddDelegateRequestBatcher<int, int>(_ => configureCalled = true);
            }
            else
            {
                services.AddDelegateRequestBatcher<int>(_ => configureCalled = true);
            }
        });

        Assert.Contains("already", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(configureCalled);
        Assert.Equal(descriptors, services.ToArray());
    }

    [Fact]
    public async Task ExecuteAsync_KeyedBatchParameter_RejectsWithoutBinding()
    {
        var services = new ServiceCollection();
        services.AddDelegateRequestBatcher<int>();
        await using var provider = services.BuildServiceProvider();
        var batcher = provider.GetRequiredService<IRequestBatcher<int>>();

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => batcher.ExecuteAsync(
            1,
            static ([FromKeyedServices("unused")] IReadOnlyList<int> requests) =>
                Assert.Fail("An invalid handler must not execute.")));

        Assert.Equal("handler", exception.ParamName);
        Assert.Contains("batch parameter", exception.Message, StringComparison.OrdinalIgnoreCase);
        await batcher.ExecuteAsync(2, static (IReadOnlyList<int> requests) =>
            Assert.Equal(2, Assert.Single(requests)));
    }

    [Fact]
    public async Task ExecuteAsync_KeyedCancellationToken_RejectsWithoutBinding()
    {
        var services = new ServiceCollection();
        services.AddDelegateRequestBatcher<int>();
        await using var provider = services.BuildServiceProvider();
        var batcher = provider.GetRequiredService<IRequestBatcher<int>>();

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => batcher.ExecuteAsync(
            1,
            static (IReadOnlyList<int> requests, [FromKeyedServices("unused")] CancellationToken token) =>
                Assert.Fail("An invalid handler must not execute.")));

        Assert.Equal("handler", exception.ParamName);
        Assert.Contains("CancellationToken", exception.Message, StringComparison.Ordinal);
        await batcher.ExecuteAsync(2, static (IReadOnlyList<int> requests) =>
            Assert.Equal(2, Assert.Single(requests)));
    }

    [Fact]
    public async Task ExecuteAsync_OptionalCancellationToken_RejectsWithoutBinding()
    {
        var services = new ServiceCollection();
        services.AddDelegateRequestBatcher<int>();
        await using var provider = services.BuildServiceProvider();
        var batcher = provider.GetRequiredService<IRequestBatcher<int>>();

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => batcher.ExecuteAsync(
            1,
            static (IReadOnlyList<int> requests, CancellationToken token = default) =>
                Assert.Fail("An invalid handler must not execute.")));

        Assert.Equal("handler", exception.ParamName);
        Assert.Contains("Optional", exception.Message, StringComparison.Ordinal);
        await batcher.ExecuteAsync(2, static (IReadOnlyList<int> requests) =>
            Assert.Equal(2, Assert.Single(requests)));
    }

    [Fact]
    public void SetResponses_PreassignedEntry_RejectsWithoutChangingOtherEntries()
    {
        IReadOnlyList<RequestBatchEntry<int, int>> entries = [new(1), new(2), new(3)];
        entries[1].SetResponse(20);

        Assert.Throws<InvalidOperationException>(() => entries.SetResponses([10, 200, 30]));

        Assert.Equal(20, entries[1].GetResponse());
        Assert.Throws<InvalidOperationException>(() => entries[0].GetResponse());
        Assert.Throws<InvalidOperationException>(() => entries[2].GetResponse());
        entries[0].SetResponse(10);
        entries[2].SetResponse(30);
        Assert.Equal([10, 20, 30], entries.Select(entry => entry.GetResponse()).ToArray());
    }
}
