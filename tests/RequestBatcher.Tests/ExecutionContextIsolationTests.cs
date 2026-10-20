using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace RequestBatcher.Tests;

public sealed class ExecutionContextIsolationTests
{
    [Fact]
    public async Task AddRequestBatcher_ResolveAndSubmit_DoesNotFlowCallerExecutionContextToHandler()
    {
        var probe = new ExecutionContextProbe();
        var services = new ServiceCollection();
        services.AddSingleton(probe);
        services.AddRequestBatcher<int, ExecutionContextHandler>(
            ServiceLifetime.Scoped,
            options => options.BatchSize = 1);

        await using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        var baselineActivity = Activity.Current;
        try
        {
            probe.Ambient.Value = "request-A";
            using (var activityA = new Activity("request-A"))
            {
                activityA.Start();
                var batcher = provider.GetRequiredService<IRequestBatcher<int>>();

                Assert.Same(activityA, Activity.Current);
                Assert.Equal("request-A", probe.Ambient.Value);

                activityA.Stop();
                Assert.Same(baselineActivity, Activity.Current);

                probe.Ambient.Value = "request-B";
                using (var activityB = new Activity("request-B"))
                {
                    activityB.Start();
                    await batcher.ExecuteAsync(1).WaitAsync(TimeSpan.FromSeconds(5));

                    Assert.Same(activityB, Activity.Current);
                    Assert.Equal("request-B", probe.Ambient.Value);
                    activityB.Stop();
                }

                Assert.Same(baselineActivity, Activity.Current);
                probe.Ambient.Value = null;
                await batcher.ExecuteAsync(2).WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Same(baselineActivity, Activity.Current);
                Assert.Null(probe.Ambient.Value);
            }

            var observations = probe.Observations.OrderBy(static observation => observation.Request).ToArray();
            Assert.Equal(2, observations.Length);
            Assert.All(observations, observation =>
            {
                Assert.Null(observation.BeforeActivityId);
                Assert.Null(observation.BeforeAmbient);
                Assert.NotNull(observation.AfterActivityId);
                Assert.Equal($"handler-{observation.Request}", observation.AfterAmbient);
            });
        }
        finally
        {
            Activity.Current = baselineActivity;
            probe.Ambient.Value = null;
        }
    }

    [Fact]
    public async Task AddRequestBatcher_ResolveWithSuppressedFlow_PreservesCallerSuppressionAndIsolatesHandler()
    {
        var probe = new ExecutionContextProbe();
        var services = new ServiceCollection();
        services.AddSingleton(probe);
        services.AddRequestBatcher<int, ExecutionContextHandler>(
            ServiceLifetime.Transient,
            options => options.BatchSize = 1);

        await using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        var baselineActivity = Activity.Current;
        try
        {
            IRequestBatcher<int> batcher;
            using (ExecutionContext.SuppressFlow())
            {
                Assert.True(ExecutionContext.IsFlowSuppressed());
                batcher = provider.GetRequiredService<IRequestBatcher<int>>();
                Assert.True(ExecutionContext.IsFlowSuppressed());
            }

            Assert.False(ExecutionContext.IsFlowSuppressed());
            probe.Ambient.Value = "restored-caller";
            await batcher.ExecuteAsync(3).WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Same(baselineActivity, Activity.Current);
            Assert.Equal("restored-caller", probe.Ambient.Value);
            var observation = Assert.Single(probe.Observations);
            Assert.Null(observation.BeforeActivityId);
            Assert.Null(observation.BeforeAmbient);
            Assert.NotNull(observation.AfterActivityId);
            Assert.Equal("handler-3", observation.AfterAmbient);
        }
        finally
        {
            Activity.Current = baselineActivity;
            probe.Ambient.Value = null;
        }
    }

    private sealed class ExecutionContextProbe
    {
        public AsyncLocal<string?> Ambient { get; } = new();

        public ConcurrentQueue<HandlerObservation> Observations { get; } = new();
    }

    private sealed class ExecutionContextHandler(ExecutionContextProbe probe)
        : IRequestBatchHandler<int>
    {
        public async ValueTask HandleAsync(
            IReadOnlyList<int> requests,
            CancellationToken cancellationToken = default)
        {
            var request = Assert.Single(requests);
            var before = new HandlerObservation(
                request,
                Activity.Current?.Id,
                probe.Ambient.Value,
                null,
                null);

            using var activity = new Activity($"handler-{request}").Start();
            probe.Ambient.Value = $"handler-{request}";
            await Task.Yield();

            probe.Observations.Enqueue(before with
            {
                AfterActivityId = Activity.Current?.Id,
                AfterAmbient = probe.Ambient.Value,
            });

            activity.Stop();
            probe.Ambient.Value = null;
        }
    }

    private sealed record HandlerObservation(
        int Request,
        string? BeforeActivityId,
        string? BeforeAmbient,
        string? AfterActivityId,
        string? AfterAmbient);
}
