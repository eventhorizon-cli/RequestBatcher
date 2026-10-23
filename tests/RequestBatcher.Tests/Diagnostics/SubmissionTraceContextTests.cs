using System.Diagnostics;
using RequestBatcher.Diagnostics;

namespace RequestBatcher.Tests.Diagnostics;

[Collection("Tracing")]
public sealed class SubmissionTraceContextTests
{
    [Fact]
    public void AddBatch_PropagationOnlyContext_DoesNotCollectReverseLinks()
    {
        var context = SubmissionTraceContext.Create(NewContext(), collectBatchLinks: false);
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
        var context = SubmissionTraceContext.Create(NewContext(), collectBatchLinks: true);
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

    [Fact]
    public void Complete_CollectingContextWithoutBatches_DoesNotAddLinks()
    {
        var context = SubmissionTraceContext.Create(NewContext(), collectBatchLinks: true);
        using var activity = new Activity("batch").Start();

        context.Complete(activity);
        context.AddBatch(NewContext());
        context.Complete(activity);

        Assert.Empty(activity.Links);
    }

    [Fact]
    public async Task AddBatch_CollectingContext_StoresConcurrentBatchesExactlyOnce()
    {
        var context = SubmissionTraceContext.Create(NewContext(), collectBatchLinks: true);
        var batches = Enumerable.Range(0, 32).Select(_ => NewContext()).ToArray();

        await Task.WhenAll(batches.Select(batch => Task.Run(() => context.AddBatch(batch))));

        using var activity = new Activity("batch").Start();
        context.Complete(activity);

        Assert.Equal(
            batches.OrderBy(batch => batch.SpanId.ToHexString()),
            activity.Links.Select(link => link.Context).OrderBy(batch => batch.SpanId.ToHexString()));
    }

    private static ActivityContext NewContext() =>
        new(ActivityTraceId.CreateRandom(), ActivitySpanId.CreateRandom(), ActivityTraceFlags.Recorded);
}
