using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace RequestBatcher.Tests;

public sealed class DelegateHandlerFormsTests
{
    private static readonly BatchHandler _storedHandler = Handle;

    [Theory]
    [InlineData("Method")]
    [InlineData("GenericMethod")]
    [InlineData("LocalFunction")]
    [InlineData("Construction")]
    [InlineData("Copy")]
    [InlineData("NestedCopy")]
    [InlineData("Variable")]
    [InlineData("Field")]
    public async Task ExecuteAsync_StaticHandlerForms_ResolveServicesAndCompleteResponses(string form)
    {
        var keyed = new Mock<IProbe>(MockBehavior.Strict);
        var ordinary = new Mock<IProbe>(MockBehavior.Strict);
        keyed.Setup(probe => probe.Record(42, It.IsAny<CancellationToken>()));
        ordinary.Setup(probe => probe.Record(42, It.IsAny<CancellationToken>()));
        var services = new ServiceCollection();
        services.AddKeyedSingleton("probe", keyed.Object);
        services.AddSingleton(ordinary.Object);
        services.AddDelegateRequestBatcher<int, string>();
        await using var provider = services.BuildServiceProvider();
        var batcher = provider.GetRequiredService<IRequestBatcher<int, string>>();

        static void Local(
            IReadOnlyList<RequestBatchEntry<int, string>> entries,
            [FromKeyedServices("probe")] IProbe keyed,
            IProbe ordinary,
            CancellationToken token) => Complete(entries, keyed, ordinary, token);

        BatchHandler variable = Handle;
        var handler = form switch
        {
            "Method" => (BatchHandler)Handle,
            "GenericMethod" => (BatchHandler)GenericHandle<int>,
            "LocalFunction" => (BatchHandler)Local,
            "Construction" => new BatchHandler(Handle),
            "Copy" => new BatchHandler(variable),
            "NestedCopy" => new BatchHandler(new BatchHandler(variable)),
            "Variable" => (Delegate)variable,
            "Field" => _storedHandler,
            _ => throw new ArgumentOutOfRangeException(nameof(form))
        };

        var result = await batcher.ExecuteAsync(42, handler);

        Assert.Equal("handled", result);
        keyed.Verify(probe => probe.Record(42, It.IsAny<CancellationToken>()), Times.Once);
        ordinary.Verify(probe => probe.Record(42, It.IsAny<CancellationToken>()), Times.Once);
        keyed.VerifyNoOtherCalls();
        ordinary.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ExecuteAsync_StaticAnonymousMethod_ResolvesServiceAndCompletesResponse()
    {
        var probe = new Mock<IProbe>(MockBehavior.Strict);
        probe.Setup(value => value.Record(42, It.IsAny<CancellationToken>()));
        var services = new ServiceCollection();
        services.AddSingleton(probe.Object);
        services.AddDelegateRequestBatcher<int, string>();
        await using var provider = services.BuildServiceProvider();
        var batcher = provider.GetRequiredService<IRequestBatcher<int, string>>();

        var result = await batcher.ExecuteAsync(42, static delegate (
            IReadOnlyList<RequestBatchEntry<int, string>> entries,
            IProbe ordinary,
            CancellationToken token)
        {
            Assert.False(token.IsCancellationRequested);
            ordinary.Record(Assert.Single(entries).Request, token);
            entries.SetResponses(static _ => "handled");
        });

        Assert.Equal("handled", result);
        probe.Verify(value => value.Record(42, It.IsAny<CancellationToken>()), Times.Once);
        probe.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("Optional", "Optional")]
    [InlineData("AsyncVoid", "Async void")]
    [InlineData("Multicast", "single")]
    public async Task ExecuteAsync_CopiesOfInvalidHandlers_FailWithoutBinding(string form, string reason)
    {
        Action<IReadOnlyList<int>, int> optional = OptionalHandle;
        Action<IReadOnlyList<int>> asyncVoid = AsyncVoidHandle;
        Action<IReadOnlyList<int>> multicast = NoOpHandle;
        multicast += OtherNoOpHandle;
        Delegate invalid = form switch
        {
            "Optional" => new Action<IReadOnlyList<int>, int>(optional),
            "AsyncVoid" => new Action<IReadOnlyList<int>>(asyncVoid),
            "Multicast" => new Action<IReadOnlyList<int>>(multicast),
            _ => throw new ArgumentOutOfRangeException(nameof(form))
        };
        var services = new ServiceCollection();
        services.AddDelegateRequestBatcher<int>();
        await using var provider = services.BuildServiceProvider();
        var batcher = provider.GetRequiredService<IRequestBatcher<int>>();

        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => batcher.ExecuteAsync(42, invalid));

        Assert.Contains(reason, exception.Message, StringComparison.OrdinalIgnoreCase);
        await batcher.ExecuteAsync(43, static (IReadOnlyList<int> values) => Assert.Equal([43], values));
    }

    [Fact]
    public async Task ExecuteAsync_EqualStaticMethodDelegates_ReuseBindingAndRejectDifferentMethod()
    {
        var keyed = new Mock<IProbe>(MockBehavior.Strict);
        var ordinary = new Mock<IProbe>(MockBehavior.Strict);
        keyed.Setup(probe => probe.Record(It.IsIn(1, 2), It.IsAny<CancellationToken>()));
        ordinary.Setup(probe => probe.Record(It.IsIn(1, 2), It.IsAny<CancellationToken>()));
        var services = new ServiceCollection();
        services.AddKeyedSingleton("probe", keyed.Object);
        services.AddSingleton(ordinary.Object);
        services.AddDelegateRequestBatcher<int, string>();
        await using var provider = services.BuildServiceProvider();
        var batcher = provider.GetRequiredService<IRequestBatcher<int, string>>();
        var first = new BatchHandler(Handle);
        var equal = new BatchHandler(Handle);
        Assert.NotSame(first, equal);
        Assert.Equal(first, equal);

        Assert.Equal("handled", await batcher.ExecuteAsync(1, first));
        Assert.Equal("handled", await batcher.ExecuteAsync(2, equal));
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => batcher.ExecuteAsync(3, (BatchHandler)OtherHandle));

        Assert.Contains("different delegate", exception.Message, StringComparison.OrdinalIgnoreCase);
        keyed.Verify(probe => probe.Record(1, It.IsAny<CancellationToken>()), Times.Once);
        keyed.Verify(probe => probe.Record(2, It.IsAny<CancellationToken>()), Times.Once);
        ordinary.Verify(probe => probe.Record(1, It.IsAny<CancellationToken>()), Times.Once);
        ordinary.Verify(probe => probe.Record(2, It.IsAny<CancellationToken>()), Times.Once);
        keyed.VerifyNoOtherCalls();
        ordinary.VerifyNoOtherCalls();
    }

    private static void OptionalHandle(IReadOnlyList<int> values, int count = 1) { }

    private static async void AsyncVoidHandle(IReadOnlyList<int> values) => await Task.Yield();

    private static void NoOpHandle(IReadOnlyList<int> values) { }

    private static void OtherNoOpHandle(IReadOnlyList<int> values) { }

    private static void Handle(
        IReadOnlyList<RequestBatchEntry<int, string>> entries,
        [FromKeyedServices("probe")] IProbe keyed,
        IProbe ordinary,
        CancellationToken token) => Complete(entries, keyed, ordinary, token);

    private static void OtherHandle(
        IReadOnlyList<RequestBatchEntry<int, string>> entries,
        [FromKeyedServices("probe")] IProbe keyed,
        IProbe ordinary,
        CancellationToken token) => Complete(entries, keyed, ordinary, token);

    private static void GenericHandle<TRequest>(
        IReadOnlyList<RequestBatchEntry<TRequest, string>> entries,
        [FromKeyedServices("probe")] IProbe keyed,
        IProbe ordinary,
        CancellationToken token) => Complete(entries, keyed, ordinary, token);

    private static void Complete<TRequest>(
        IReadOnlyList<RequestBatchEntry<TRequest, string>> entries,
        IProbe keyed,
        IProbe ordinary,
        CancellationToken token)
    {
        Assert.False(token.IsCancellationRequested);
        foreach (var entry in entries)
        {
            keyed.Record(entry.Request!, token);
            ordinary.Record(entry.Request!, token);
        }

        entries.SetResponses(static _ => "handled");
    }

    private delegate void BatchHandler(
        IReadOnlyList<RequestBatchEntry<int, string>> entries,
        IProbe keyed,
        IProbe ordinary,
        CancellationToken token);

    public interface IProbe
    {
        void Record(object request, CancellationToken cancellationToken);
    }
}
