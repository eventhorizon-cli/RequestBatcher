using System.Diagnostics;

namespace RequestBatcher.Diagnostics;

internal sealed class SubmissionTraceContext(ActivityContext context, bool collectBatchLinks)
{
    private readonly object _gate = new();
    private List<ActivityContext>? _batches;
    private bool _completed;

    internal ActivityContext Context { get; } = context;

    internal void AddBatch(ActivityContext batch)
    {
        if (!collectBatchLinks)
        {
            return;
        }

        lock (_gate)
        {
            if (!_completed)
            {
                (_batches ??= []).Add(batch);
            }
        }
    }

    internal void Complete(Activity activity)
    {
        List<ActivityContext>? batches;
        lock (_gate)
        {
            _completed = true;
            batches = _batches;
            _batches = null;
        }

        if (batches is not null)
        {
            foreach (var batch in batches)
            {
                activity.AddLink(new ActivityLink(batch));
            }
        }
    }
}
