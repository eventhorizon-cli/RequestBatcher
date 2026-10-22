using System.Diagnostics;
using RequestBatcher.Diagnostics;

namespace RequestBatcher.Tests.Diagnostics;

[Collection("Tracing")]
public sealed class SubmissionTraceContextTests
{
    [Fact]
    public void AddBatch_PropagationOnlyContext_DoesNotCollectReverseLinks()
    {
        var context = new SubmissionTraceContext(NewContext(), collectBatchLinks: false);
        using var activity = new Activity("batch").Start();

        context.AddBatch(NewContext());
        context.Complete(activity);

        Assert.False(context.CollectBatchLinks);
        Assert.Empty(activity.Links);
    }

    [Fact]
    public void Complete_CollectingContext_FlushesLinksOnceAndIgnoresLaterBatches()
    {
        var firstBatch = NewContext();
        var secondBatch = NewContext();
        var laterBatch = NewContext();
        var context = new SubmissionTraceContext(NewContext(), collectBatchLinks: true);
        using var activity = new Activity("batch").Start();

        context.AddBatch(firstBatch);
        context.AddBatch(secondBatch);
        context.Complete(activity);

        Assert.Equal(
            new[] { firstBatch, secondBatch },
            activity.Links.Select(link => link.Context));

        context.AddBatch(laterBatch);
        context.Complete(activity);

        Assert.Equal(
            new[] { firstBatch, secondBatch },
            activity.Links.Select(link => link.Context));
    }

    private static ActivityContext NewContext() =>
        new(ActivityTraceId.CreateRandom(), ActivitySpanId.CreateRandom(), ActivityTraceFlags.Recorded);
}
