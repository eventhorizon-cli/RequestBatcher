using System.Collections;
using System.Diagnostics;

namespace RequestBatcher.Diagnostics.Tracing;

internal sealed class BatchTraceLinks : IEnumerable<ActivityLink>
{
    private readonly SubmissionTraceContext _firstSubmission;
    private readonly (ActivityTraceId TraceId, ActivitySpanId SpanId) _firstKey;
    private Dictionary<(ActivityTraceId TraceId, ActivitySpanId SpanId), SubmissionTraceContext>? _additionalSubmissions;

    internal BatchTraceLinks(SubmissionTraceContext firstSubmission)
    {
        _firstSubmission = firstSubmission;
        _firstKey = GetKey(firstSubmission.Context);
    }

    internal int Count => 1 + (_additionalSubmissions?.Count ?? 0);

    internal void Add(SubmissionTraceContext submission)
    {
        var key = GetKey(submission.Context);
        if (key == _firstKey)
        {
            return;
        }

        var submissions = _additionalSubmissions ??= [];
        submissions.TryAdd(key, submission);
    }

    internal void AddBatch(ActivityContext batch)
    {
        _firstSubmission.AddBatch(batch);
        if (_additionalSubmissions is null)
        {
            return;
        }

        foreach (var submission in _additionalSubmissions)
        {
            submission.Value.AddBatch(batch);
        }
    }

    public IEnumerator<ActivityLink> GetEnumerator()
    {
        yield return new ActivityLink(_firstSubmission.Context);

        if (_additionalSubmissions is null)
        {
            yield break;
        }

        foreach (var submission in _additionalSubmissions)
        {
            yield return new ActivityLink(submission.Value.Context);
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private static (ActivityTraceId TraceId, ActivitySpanId SpanId) GetKey(ActivityContext context) =>
        (context.TraceId, context.SpanId);
}
