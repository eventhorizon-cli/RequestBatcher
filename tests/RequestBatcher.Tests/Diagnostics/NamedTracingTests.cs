using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using RequestBatcher.Diagnostics;

namespace RequestBatcher.Tests.Diagnostics;

[Collection("Tracing")]
public sealed class NamedTracingTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task ExecuteAsync_MultipleNamedAndUnnamedPipelines_TagsBothSpansWithPublicName(
        bool useDelegate, bool hasResponse, bool submitBatch)
    {
        var sampled = new ConcurrentQueue<(string Operation, string? Name)>();
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == RequestBatcherTracing.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> options) =>
            {
                sampled.Enqueue((options.Name,
                    options.Tags?.SingleOrDefault(tag => tag.Key == "requestbatcher.name").Value as string));
                return ActivitySamplingResult.AllDataAndRecorded;
            },
            ActivityStopped = stopped.Enqueue,
        };
        ActivitySource.AddActivityListener(listener);

        var responseHandler = new Mock<IRequestBatchHandler<int, string>>(MockBehavior.Strict);
        responseHandler.Setup(handler => handler.HandleAsync(
                It.IsAny<IReadOnlyList<RequestBatchEntry<int, string>>>(), It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<RequestBatchEntry<int, string>>, CancellationToken>(
                (entries, _) => entries.SetResponses(static request => request.ToString()))
            .Returns(ValueTask.CompletedTask);
        var handler = new Mock<IRequestBatchHandler<int>>(MockBehavior.Strict);
        handler.Setup(value => value.HandleAsync(It.IsAny<IReadOnlyList<int>>(), It.IsAny<CancellationToken>()))
            .Returns(ValueTask.CompletedTask);
        var services = new ServiceCollection();
        services.AddSingleton(responseHandler);
        services.AddSingleton(handler);
        string?[] names = [null, "Alpha", "beta"];
        foreach (var name in names)
        {
            if (useDelegate && hasResponse)
            {
                if (name is null)
                {
                    services.AddDelegateRequestBatcher<int, string>();
                }
                else
                {
                    services.AddDelegateRequestBatcher<int, string>(name);
                }
            }
            else if (useDelegate)
            {
                if (name is null)
                {
                    services.AddDelegateRequestBatcher<int>();
                }
                else
                {
                    services.AddDelegateRequestBatcher<int>(name);
                }
            }
            else if (hasResponse)
            {
                if (name is null)
                {
                    services.AddRequestBatcher<int, string, ResponseHandler>(ServiceLifetime.Singleton);
                }
                else
                {
                    services.AddRequestBatcher<int, string, ResponseHandler>(name, ServiceLifetime.Singleton);
                }
            }
            else
            {
                if (name is null)
                {
                    services.AddRequestBatcher<int, Handler>(ServiceLifetime.Singleton);
                }
                else
                {
                    services.AddRequestBatcher<int, Handler>(name, ServiceLifetime.Singleton);
                }
            }
        }

        await using var provider = services.BuildServiceProvider();
        await Task.WhenAll(names.Select(async name =>
        {
            using var caller = new Activity(name ?? "unnamed").Start();
            if (hasResponse)
            {
                var batcher = name is null ? provider.GetRequiredService<IRequestBatcher<int, string>>()
                    : provider.GetRequiredKeyedService<IRequestBatcher<int, string>>(name);
                if (submitBatch)
                {
                    var result = useDelegate
                        ? await batcher.ExecuteAsync(new[] { 1, 2 },
                            static (IReadOnlyList<RequestBatchEntry<int, string>> entries) =>
                                entries.SetResponses(static request => request.ToString()))
                        : await batcher.ExecuteAsync(new[] { 1, 2 });
                    Assert.Equal(new[] { "1", "2" }, result);
                }
                else
                {
                    var result = useDelegate
                        ? await batcher.ExecuteAsync(1,
                            static (IReadOnlyList<RequestBatchEntry<int, string>> entries) =>
                                entries.SetResponses(static request => request.ToString()))
                        : await batcher.ExecuteAsync(1);
                    Assert.Equal("1", result);
                }
            }
            else
            {
                var batcher = name is null ? provider.GetRequiredService<IRequestBatcher<int>>()
                    : provider.GetRequiredKeyedService<IRequestBatcher<int>>(name);
                if (submitBatch)
                {
                    if (useDelegate)
                    {
                        await batcher.ExecuteAsync(new[] { 1, 2 }, static (IReadOnlyList<int> _) => { });
                    }
                    else
                    {
                        await batcher.ExecuteAsync(new[] { 1, 2 });
                    }
                }
                else
                {
                    if (useDelegate)
                    {
                        await batcher.ExecuteAsync(1, static (IReadOnlyList<int> _) => { });
                    }
                    else
                    {
                        await batcher.ExecuteAsync(1);
                    }
                }
            }

            Assert.Same(caller, Activity.Current);
            var expectedCallName = name is null ? "RequestBatcher.Execute" : $"RequestBatcher.Execute {name}";
            var call = Assert.Single(stopped, span => span.OperationName == expectedCallName
                && span.ParentSpanId == caller.SpanId);
            Assert.Equal(name, call.GetTagItem("requestbatcher.name"));
        }));

        var calls = stopped.Where(span => span.OperationName == RequestBatcherTracing.GetCallName(
            span.GetTagItem("requestbatcher.name") as string)).ToArray();
        Assert.Equal(3, calls.Length);
        var batches = stopped.Where(span => span.OperationName == RequestBatcherTracing.GetBatchName(
            span.GetTagItem("requestbatcher.name") as string)).ToArray();
        Assert.NotEmpty(batches);
        foreach (var batch in batches)
        {
            var link = Assert.Single(batch.Links);
            var call = Assert.Single(calls, span => span.Context == link.Context);
            Assert.Equal(call.GetTagItem("requestbatcher.name"), batch.GetTagItem("requestbatcher.name"));
        }

        foreach (var name in names)
        {
            Assert.Contains(sampled, item => item ==
                (name is null ? "RequestBatcher.Execute" : $"RequestBatcher.Execute {name}", name));
            Assert.Contains(sampled, item => item ==
                (name is null ? "RequestBatcher.HandleBatch" : $"RequestBatcher.HandleBatch {name}", name));
        }

        Assert.All(stopped.Where(span => span.GetTagItem("requestbatcher.name") is null),
            span => Assert.DoesNotContain(span.TagObjects, tag => tag.Key == "requestbatcher.name"));
    }

    private sealed class Handler(Mock<IRequestBatchHandler<int>> handler) : IRequestBatchHandler<int>
    {
        public ValueTask HandleAsync(IReadOnlyList<int> requests, CancellationToken cancellationToken = default) =>
            handler.Object.HandleAsync(requests, cancellationToken);
    }

    private sealed class ResponseHandler(Mock<IRequestBatchHandler<int, string>> handler) : IRequestBatchHandler<int, string>
    {
        public ValueTask HandleAsync(IReadOnlyList<RequestBatchEntry<int, string>> entries,
            CancellationToken cancellationToken = default) => handler.Object.HandleAsync(entries, cancellationToken);
    }
}
