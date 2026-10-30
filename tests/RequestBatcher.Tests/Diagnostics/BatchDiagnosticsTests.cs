using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace RequestBatcher.Tests.Diagnostics;

[Collection("Tracing")]
public sealed class BatchDiagnosticsTests
{
    private const string BatcherName = "batch-diagnostics-regression";
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_HandlerCompletes_EmitsDiagnosticsBeforeCallerCompletes(bool fail)
    {
        var entered = NewSource();
        var release = NewSource();
        var stoppedActivities = new ConcurrentQueue<Activity>();
        var measurements = new ConcurrentQueue<Measurement>();
        using var activityListener = new ActivityListener
        {
            ShouldListenTo = static source => source.Name == "RequestBatcher",
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (Equals(activity.GetTagItem("requestbatcher.name"), BatcherName))
                {
                    stoppedActivities.Enqueue(activity);
                }
            },
        };
        ActivitySource.AddActivityListener(activityListener);
        using var meterListener = new MeterListener
        {
            InstrumentPublished = static (instrument, listener) =>
            {
                if (instrument.Meter.Name == "RequestBatcher" &&
                    instrument.Name is "requestbatcher.batch.size" or "requestbatcher.handle.duration")
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            },
        };
        meterListener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            if (instrument.Name == "requestbatcher.batch.size" && HasBatcherName(tags, BatcherName))
            {
                measurements.Enqueue(new Measurement(instrument.Name, value));
            }
        });
        meterListener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
        {
            if (instrument.Name == "requestbatcher.handle.duration" && HasBatcherName(tags, BatcherName))
            {
                measurements.Enqueue(new Measurement(instrument.Name, value));
            }
        });
        meterListener.Start();

        var expected = new InvalidOperationException("handler failed");
        var handler = new Mock<IRequestBatchHandler<int>>(MockBehavior.Strict);
        handler.Setup(value => value.HandleAsync(
                It.IsAny<IReadOnlyList<int>>(), It.IsAny<CancellationToken>()))
            .Returns(async (IReadOnlyList<int> requests, CancellationToken _) =>
            {
                entered.TrySetResult();
                await release.Task;
                if (fail)
                {
                    throw expected;
                }
            });
        var services = new ServiceCollection();
        services.AddSingleton(handler);
        services.AddRequestBatcher<int, HandlerProxy>(BatcherName, ServiceLifetime.Singleton,
            options => options.BatchSize = 1);
        await using var serviceProvider = services.BuildServiceProvider();
        var batcher = serviceProvider.GetRequiredKeyedService<IRequestBatcher<int>>(BatcherName);
        var request = batcher.ExecuteAsync(42);
        var atCompletion = request.ContinueWith(
            _ => new CompletionObservation(
                stoppedActivities.Where(IsBatchActivity).ToArray(),
                measurements.ToArray()),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        try
        {
            await entered.Task.WaitAsync(_timeout);
            release.TrySetResult();

            if (fail)
            {
                Assert.Same(expected, await Assert.ThrowsAsync<InvalidOperationException>(
                    () => request.WaitAsync(_timeout)));
            }
            else
            {
                await request.WaitAsync(_timeout);
            }

            var observed = await atCompletion.WaitAsync(_timeout);
            var batch = Assert.Single(observed.BatchActivities);
            Assert.Equal(fail ? ActivityStatusCode.Error : ActivityStatusCode.Unset, batch.Status);

            var batchSize = Assert.Single(observed.Measurements,
                item => item.Name == "requestbatcher.batch.size");
            Assert.Equal(1, batchSize.Value);
            var duration = Assert.Single(observed.Measurements,
                item => item.Name == "requestbatcher.handle.duration");
            Assert.True(duration.Value >= 0);
        }
        finally
        {
            release.TrySetResult();
        }

        handler.Verify(value => value.HandleAsync(
            It.Is<IReadOnlyList<int>>(requests => requests.SequenceEqual(new[] { 42 })),
            It.IsAny<CancellationToken>()), Times.Once);
        handler.VerifyNoOtherCalls();
    }

    private static bool IsBatchActivity(Activity activity) =>
        activity.OperationName == $"RequestBatcher.Handle {BatcherName}";

    private static bool HasBatcherName(
        ReadOnlySpan<KeyValuePair<string, object?>> tags,
        string expected)
    {
        foreach (var tag in tags)
        {
            if (tag.Key == "requestbatcher.name" && Equals(tag.Value, expected))
            {
                return true;
            }
        }

        return false;
    }

    private static TaskCompletionSource NewSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed record Measurement(string Name, double Value);

    private sealed record CompletionObservation(Activity[] BatchActivities, Measurement[] Measurements);

    private sealed class HandlerProxy(Mock<IRequestBatchHandler<int>> handler) : IRequestBatchHandler<int>
    {
        public ValueTask HandleAsync(
            IReadOnlyList<int> requests,
            CancellationToken cancellationToken = default) =>
            handler.Object.HandleAsync(requests, cancellationToken);
    }
}
