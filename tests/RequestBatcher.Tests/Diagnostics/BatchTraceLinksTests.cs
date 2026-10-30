using System.Diagnostics;
using RequestBatcher.Diagnostics.Tracing;

namespace RequestBatcher.Tests.Diagnostics;

[Collection("Tracing")]
public sealed class BatchTraceLinksTests
{
    [Fact]
    public void Enumerate_OnlyFirstSubmission_ReturnsOneRepeatableLink()
    {
        var first = SubmissionTraceContext.Create(NewContext(), collectBatchLinks: false);
        var links = new BatchTraceLinks(first);

        var firstEnumeration = links.ToArray();
        var secondEnumeration = links.ToArray();

        Assert.Equal(new[] { first.Context }, firstEnumeration.Select(link => link.Context));
        Assert.Equal(firstEnumeration, secondEnumeration);
    }

    [Fact]
    public void Add_DuplicateContexts_PreservesFirstSubmissionAndUniqueLinks()
    {
        var firstContext = NewContext();
        var secondContext = NewContext();
        var first = SubmissionTraceContext.Create(firstContext, collectBatchLinks: true);
        var duplicateFirst = SubmissionTraceContext.Create(firstContext, collectBatchLinks: true);
        var second = SubmissionTraceContext.Create(secondContext, collectBatchLinks: true);
        var duplicateSecond = SubmissionTraceContext.Create(secondContext, collectBatchLinks: true);
        var links = new BatchTraceLinks(first);

        links.Add(duplicateFirst);
        links.Add(second);
        links.Add(duplicateSecond);
        links.Add(first);

        Assert.Equal(
            new[] { firstContext, secondContext },
            links.Select(link => link.Context));

        var batch = NewContext();
        links.AddBatch(batch);
        using var firstActivity = new Activity("first").Start();
        using var duplicateFirstActivity = new Activity("duplicate-first").Start();
        using var secondActivity = new Activity("second").Start();
        using var duplicateSecondActivity = new Activity("duplicate-second").Start();
        first.Complete(firstActivity);
        duplicateFirst.Complete(duplicateFirstActivity);
        second.Complete(secondActivity);
        duplicateSecond.Complete(duplicateSecondActivity);

        Assert.Equal(new[] { batch }, firstActivity.Links.Select(link => link.Context));
        Assert.Empty(duplicateFirstActivity.Links);
        Assert.Equal(new[] { batch }, secondActivity.Links.Select(link => link.Context));
        Assert.Empty(duplicateSecondActivity.Links);
    }

    [Fact]
    public void Add_RepeatedLargeInput_DoesNotMutateRepeatedEnumeration()
    {
        var first = SubmissionTraceContext.Create(NewContext(), collectBatchLinks: false);
        var second = SubmissionTraceContext.Create(NewContext(), collectBatchLinks: false);
        var links = new BatchTraceLinks(first);

        for (var index = 0; index < 1000; index++)
        {
            links.Add(first);
            links.Add(second);
        }

        var expected = links.ToArray();

        Assert.Equal(2, expected.Length);
        Assert.Equal(expected, links.ToArray());
    }

    [Fact]
    public void AddBatch_PropagationOnlySubmissions_DoNotCollectReverseLinks()
    {
        var first = SubmissionTraceContext.Create(NewContext(), collectBatchLinks: false);
        var second = SubmissionTraceContext.Create(NewContext(), collectBatchLinks: false);
        var links = new BatchTraceLinks(first);
        links.Add(second);
        links.AddBatch(NewContext());

        using var firstActivity = new Activity("first").Start();
        using var secondActivity = new Activity("second").Start();
        first.Complete(firstActivity);
        second.Complete(secondActivity);

        Assert.Empty(firstActivity.Links);
        Assert.Empty(secondActivity.Links);
    }

    private static ActivityContext NewContext() =>
        new(ActivityTraceId.CreateRandom(), ActivitySpanId.CreateRandom(), ActivityTraceFlags.Recorded);
}
