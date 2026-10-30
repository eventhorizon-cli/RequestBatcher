using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using RequestBatcher.Diagnostics.Metrics;

namespace RequestBatcher.Tests.Diagnostics;

[Collection("Tracing")]
public sealed class MetricsShutdownTests
{
    private static TimeSpan Timeout => TimeSpan.FromSeconds(10);

    [Fact]
    public async Task DisposeAsync_PendingNonGenericMeasurement_WaitsAndSharesDisposalCompletion()
    {
        await using var metrics = new RequestBatcherMetrics([new(1)]);
        var measurements = new ConcurrentQueue<string>();
        using var listener = CreateListener(measurements);
        var release = NewSource();
        var calledAfterDispose = false;

        var execution = metrics.MeasureExecution(
            "Int32",
            null,
            "single",
            42,
            async _ => await release.Task);

        var firstDispose = metrics.DisposeAsync().AsTask();
        var repeatedDispose = metrics.DisposeAsync().AsTask();
        try
        {
            Assert.False(firstDispose.IsCompleted);
            Assert.False(repeatedDispose.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
        }

        await execution.WaitAsync(Timeout);
        await Task.WhenAll(firstDispose, repeatedDispose).WaitAsync(Timeout);

        await metrics.MeasureExecution("Int32", null, "single", 43, _ =>
        {
            calledAfterDispose = true;
            return Task.CompletedTask;
        }).WaitAsync(Timeout);

        Assert.True(calledAfterDispose);
        Assert.Equal(new[] { "success" }, measurements);
    }

    [Fact]
    public async Task DisposeAsync_PendingGenericMeasurement_WaitsBeforePublishingCompletion()
    {
        await using var metrics = new RequestBatcherMetrics([new(1)]);
        var measurements = new ConcurrentQueue<string>();
        using var listener = CreateListener(measurements);
        var release = NewSource();

        var execution = metrics.MeasureExecution<int, int>(
            "Int32",
            null,
            "multi",
            42,
            async request =>
            {
                await release.Task;
                return request * 2;
            });

        var disposal = metrics.DisposeAsync().AsTask();
        try
        {
            Assert.False(disposal.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
        }

        Assert.Equal(84, await execution.WaitAsync(Timeout));
        await disposal.WaitAsync(Timeout);

        Assert.Equal(new[] { "success" }, measurements);
    }

    [Fact]
    public async Task DisposeAsync_MixedCompletedOutcomes_DrainsSuccessFailureCancellationAndSynchronousThrow()
    {
        await using var metrics = new RequestBatcherMetrics([new(1)]);
        var outcomes = new ConcurrentQueue<string>();
        using var listener = CreateListener(outcomes);
        var release = NewSource();
        using var cancellation = new CancellationTokenSource();
        var expectedFailure = new InvalidOperationException("async failure");
        var synchronousFailure = new InvalidOperationException("synchronous failure");

        var success = metrics.MeasureExecution<int>(
            "Int32", null, "single", 1, async _ => await release.Task);
        var failure = metrics.MeasureExecution<int>(
            "Int32", null, "single", 2, async _ =>
            {
                await release.Task;
                throw expectedFailure;
            });
        var canceled = metrics.MeasureExecution<int>(
            "Int32", null, "single", 3, async _ =>
            {
                await release.Task;
                cancellation.Token.ThrowIfCancellationRequested();
            });

        var actualSynchronousFailure = Assert.Throws<InvalidOperationException>(() =>
        {
            _ = metrics.MeasureExecution<int>("Int32", null, "single", 4, _ => throw synchronousFailure);
        });
        Assert.Same(synchronousFailure, actualSynchronousFailure);

        var disposal = metrics.DisposeAsync().AsTask();
        try
        {
            Assert.False(disposal.IsCompleted);
        }
        finally
        {
            cancellation.Cancel();
            release.TrySetResult();
        }

        await success.WaitAsync(Timeout);
        Assert.Same(expectedFailure, await Assert.ThrowsAsync<InvalidOperationException>(() => failure.WaitAsync(Timeout)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled.WaitAsync(Timeout));
        await disposal.WaitAsync(Timeout);

        Assert.Equal(new[] { "canceled", "error", "error", "success" }, outcomes.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task DisposeAsync_ListenerDisabledWhileMeasurementPending_DoesNotWaitForMeasurementCallback()
    {
        await using var metrics = new RequestBatcherMetrics([new(1)]);
        var measurementCount = 0;
        Instrument? executeDuration = null;
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, subscriber) =>
            {
                if (instrument.Meter.Name == RequestBatcherMetrics.MeterName &&
                    instrument.Name == "requestbatcher.execute.duration")
                {
                    executeDuration = instrument;
                    subscriber.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<double>((_, _, _, _) => Interlocked.Increment(ref measurementCount));
        listener.Start();
        Assert.NotNull(executeDuration);

        var release = NewSource();
        var execution = metrics.MeasureExecution<int>(
            "Int32", null, "single", 1, async _ => await release.Task);
        var disposal = metrics.DisposeAsync().AsTask();
        try
        {
            Assert.False(disposal.IsCompleted);
        }
        finally
        {
            listener.DisableMeasurementEvents(executeDuration!);
            release.TrySetResult();
        }

        await execution.WaitAsync(Timeout);
        await disposal.WaitAsync(Timeout);

        Assert.Equal(0, Volatile.Read(ref measurementCount));
    }

    [Fact]
    public async Task MeasureExecution_AfterDisposal_RunsGenericAndNonGenericDelegatesWithoutMeasuring()
    {
        await using var metrics = new RequestBatcherMetrics([new(1)]);
        var measurements = new ConcurrentQueue<string>();
        using var listener = CreateListener(measurements);
        await metrics.DisposeAsync();
        var nonGenericCalled = false;
        var genericCalled = false;

        await metrics.MeasureExecution("Int32", null, "single", 1, _ =>
        {
            nonGenericCalled = true;
            return Task.CompletedTask;
        });
        var result = await metrics.MeasureExecution<int, int>("Int32", null, "multi", 2, request =>
        {
            genericCalled = true;
            return Task.FromResult(request * 2);
        });

        Assert.True(nonGenericCalled);
        Assert.True(genericCalled);
        Assert.Equal(4, result);
        Assert.Empty(measurements);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProviderDisposeAsync_MixedResponseCalls_RecordsEveryCallAndPreservesResponses(bool useDelegate)
    {
        var measurementCount = 0;
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, subscriber) =>
            {
                if (instrument.Meter.Name == RequestBatcherMetrics.MeterName &&
                    instrument.Name == "requestbatcher.execute.duration")
                {
                    subscriber.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<double>((_, _, _, _) => Interlocked.Increment(ref measurementCount));
        listener.Start();

        var gate = new Gate(NewSource(), NewSource());
        var services = new ServiceCollection();
        if (useDelegate)
        {
            services.AddDelegateRequestBatcher<ResponseRequest, int>(options =>
            {
                options.BatchSize = 1;
                options.MaxConcurrency = 1;
            });
        }
        else
        {
            services.AddSingleton(gate);
            services.AddRequestBatcher<ResponseRequest, int, ResponseHandler>(ServiceLifetime.Singleton, options =>
            {
                options.BatchSize = 1;
                options.MaxConcurrency = 1;
            });
        }

        var provider = services.BuildServiceProvider();
        var batcher = provider.GetRequiredService<IRequestBatcher<ResponseRequest, int>>();
        Task? disposal = null;
        try
        {
            Delegate responseHandler = static async (
                IReadOnlyList<RequestBatchEntry<ResponseRequest, int>> entries) =>
            {
                var first = entries[0].Request;
                first.Gate.Entered.TrySetResult();
                await first.Gate.Release.Task;
                entries.SetResponses(static request => request.Value * 10);
            };
            var first = useDelegate
                ? batcher.ExecuteAsync(new ResponseRequest(-1, gate), responseHandler)
                : batcher.ExecuteAsync(new ResponseRequest(-1, gate));

            await gate.Entered.Task.WaitAsync(Timeout);
            var single = useDelegate
                ? batcher.ExecuteAsync(new ResponseRequest(1, gate), responseHandler)
                : batcher.ExecuteAsync(new ResponseRequest(1, gate));
            var multiple = useDelegate
                ? batcher.ExecuteAsync([new ResponseRequest(2, gate), new ResponseRequest(3, gate)], responseHandler)
                : batcher.ExecuteAsync([new ResponseRequest(2, gate), new ResponseRequest(3, gate)]);
            disposal = provider.DisposeAsync().AsTask();
            Assert.False(disposal.IsCompleted);

            gate.Release.TrySetResult();
            Assert.Equal(-10, await first.WaitAsync(Timeout));
            Assert.Equal(10, await single.WaitAsync(Timeout));
            Assert.Equal(new[] { 20, 30 }, await multiple.WaitAsync(Timeout));
            await disposal.WaitAsync(Timeout);

            Assert.Equal(3, Volatile.Read(ref measurementCount));
        }
        finally
        {
            gate.Release.TrySetResult();
            disposal ??= provider.DisposeAsync().AsTask();
            await disposal.WaitAsync(Timeout);
        }
    }

    [Fact]
    public async Task DisposeAsync_WithAcceptedCalls_RecordsEveryExecuteDuration()
    {
        var count = 0;
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, subscriber) =>
            {
                if (instrument.Meter.Name == RequestBatcherMetrics.MeterName &&
                    instrument.Name == "requestbatcher.execute.duration")
                {
                    subscriber.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<double>((_, _, _, _) => Interlocked.Increment(ref count));
        listener.Start();
        var expected = 0;
        for (var round = 0; round < 100; round++)
        {
            var release = NewSource();
            var entered = NewSource();
            var services = new ServiceCollection();
            services.AddSingleton(new Gate(entered, release));
            services.AddRequestBatcher<int, Handler>(ServiceLifetime.Singleton, options =>
            {
                options.BatchSize = 256;
                options.MaxConcurrency = 8;
            });
            await using var provider = services.BuildServiceProvider();
            var batcher = provider.GetRequiredService<IRequestBatcher<int>>();
            try
            {
                var calls = Enumerable.Range(0, 256).Select(request => batcher.ExecuteAsync(request)).ToArray();
                await entered.Task.WaitAsync(Timeout);
                var disposal = provider.DisposeAsync().AsTask();
                release.TrySetResult();
                await disposal.WaitAsync(Timeout);
                await Task.WhenAll(calls).WaitAsync(Timeout);
                expected += calls.Length;
            }
            finally
            {
                release.TrySetResult();
            }
        }

        Assert.Equal(expected, count);
    }

    private static MeterListener CreateListener(ConcurrentQueue<string> outcomes)
    {
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, subscriber) =>
            {
                if (instrument.Meter.Name == RequestBatcherMetrics.MeterName &&
                    instrument.Name == "requestbatcher.execute.duration")
                {
                    subscriber.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<double>((_, _, tags, _) =>
        {
            var outcome = tags.ToArray().Single(tag => tag.Key == "requestbatcher.outcome").Value;
            outcomes.Enqueue((string)outcome!);
        });
        listener.Start();
        return listener;
    }

    private static TaskCompletionSource NewSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed record Gate(TaskCompletionSource Entered, TaskCompletionSource Release);

    private sealed record ResponseRequest(int Value, Gate Gate);

    private sealed class Handler(Gate gate) : IRequestBatchHandler<int>
    {
        public async ValueTask HandleAsync(
            IReadOnlyList<int> requests,
            CancellationToken cancellationToken = default)
        {
            gate.Entered.TrySetResult();
            await gate.Release.Task;
        }
    }

    private sealed class ResponseHandler(Gate gate) : IRequestBatchHandler<ResponseRequest, int>
    {
        public async ValueTask HandleAsync(
            IReadOnlyList<RequestBatchEntry<ResponseRequest, int>> entries,
            CancellationToken cancellationToken = default)
        {
            gate.Entered.TrySetResult();
            await gate.Release.Task;
            entries.SetResponses(static request => request.Value * 10);
        }
    }
}
