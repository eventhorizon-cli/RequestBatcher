using System.Diagnostics;
using RequestBatcher.Scheduling;

namespace RequestBatcher.Tests.Scheduling;

public sealed class ConsumerTaskMonitorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Monitor_ConsumerStopsWithoutShutdown_ReportsUnexpectedStop(bool canceled)
    {
        var consumer = canceled ? Task.FromCanceled(new CancellationToken(true)) : Task.CompletedTask;
        Exception? reported = null;
        var callbackCount = 0;
        var monitor = new ConsumerTaskMonitor([consumer], CancellationToken.None, "test-request", exception =>
        {
            reported = exception;
            Interlocked.Increment(ref callbackCount);
        });

        await monitor.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        var actual = Assert.IsType<InvalidOperationException>(reported);
        Assert.Contains("test-request", actual.Message);
        Assert.Contains("stopped unexpectedly", actual.Message);
        Assert.Equal(1, callbackCount);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task Monitor_ConsumerFails_InvokesCallbackWithoutInitializingContext(
        bool alreadyCompleted,
        bool alreadySuppressed)
    {
        using var activity = new Activity("initializing-request").Start();
        var ambient = new AsyncLocal<string?> { Value = "initializing-request" };
        var consumer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var expected = new InvalidOperationException("Consumer failed.");
        if (alreadyCompleted)
        {
            consumer.SetException(expected);
        }

        Exception? observedException = null;
        var observedActivity = activity;
        var observedAmbient = ambient.Value;
        var observedSuppression = true;
        ConsumerTaskMonitor monitor;
        using (alreadySuppressed ? ExecutionContext.SuppressFlow() : default)
        {
            monitor = new ConsumerTaskMonitor([consumer.Task], CancellationToken.None, "test", exception =>
            {
                observedException = exception;
                observedActivity = Activity.Current;
                observedAmbient = ambient.Value;
                observedSuppression = ExecutionContext.IsFlowSuppressed();
            });
            Assert.Equal(alreadySuppressed, ExecutionContext.IsFlowSuppressed());
            Assert.Same(activity, Activity.Current);
            Assert.Equal("initializing-request", ambient.Value);
        }

        if (!alreadyCompleted)
        {
            consumer.SetException(expected);
        }

        await monitor.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Same(expected, observedException);
        Assert.Null(observedActivity);
        Assert.Null(observedAmbient);
        Assert.False(observedSuppression);
        Assert.False(ExecutionContext.IsFlowSuppressed());
        Assert.Same(activity, Activity.Current);
        Assert.Equal("initializing-request", ambient.Value);
    }
}
